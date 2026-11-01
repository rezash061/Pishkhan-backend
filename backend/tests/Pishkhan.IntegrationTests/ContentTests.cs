using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Modules.Content.Services;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.13 part 2: the CMS in the back office, the website's public reads, previews, scheduled publishing and revalidation.</summary>
[Collection(WorkersCollection.Name)]
public sealed class ContentTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay (cache invalidation to the Api host, revalidation calls to the fake site).</summary>
    private readonly ChannelsHost _workers = workers;
    private ApiFactory _api = null!;
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        _admin = await AdminHost.StartAsync(infra, Ct);

        // The Api host's cache invalidation consumer starts at the latest offset: wait until it is connected.
        var bus = _api.Services.GetRequiredService<IBusControl>();
        var watch = Stopwatch.StartNew();
        while (bus.CheckHealth().Status != BusHealthStatus.Healthy && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(100, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> Code(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString()!;

    /// <summary>Polls a public URL until <paramref name="done"/> holds (the Api host's cache drops on the invalidation event).</summary>
    private async Task<HttpResponseMessage> EventuallyAsync(string url, Func<HttpResponseMessage, string, bool> done, string because)
    {
        var site = _api.CreateClient();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var response = await site.GetAsync(url, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (done(response, text))
            {
                return response;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new ShouldAssertException($"Timed out waiting for {because}: {(int)response.StatusCode} {text}");
            }

            await Task.Delay(200, Ct);
        }
    }

    private static object[] Blocks(string text) => [new { type = "h2", text = "مقدمه", id = "intro" }, new { type = "p", text }, new { type = "ul", items = new[] { "یک", "دو" } }];

    [Fact]
    public async Task Editors_publish_posts_and_the_website_reads_them_with_previews_and_revalidation()
    {
        var editor = _admin.StaffClient(StaffRoles.ContentEditor);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var category = await Json(await editor.PostAsJsonAsync("/admin/v1/content/categories", new { kind = "blog", slug = $"guides-{tag}", title = "راهنماها", sortOrder = 1 }, Ct), HttpStatusCode.Created);
        var categorySlug = category.GetProperty("slug").GetString()!;

        // Blocks are checked: allowed types, and links must be https or site paths.
        (await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "post", slug = "x", title = "t", body = new[] { new { type = "script", text = "x" } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var badLink = await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "post", slug = "x", title = "t", body = new[] { new { type = "cta", text = "x", href = "javascript:alert(1)" } } }, Ct);
        (await Json(badLink, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("body")[0].GetString().ShouldBe("validation.content.link");
        (await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "post", slug = "Bad Slug", title = "t" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "post", slug = "x", title = "t", category = "nope" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var slug = $"اتوماسیون-دایرکت-{tag}";
        var created = await Json(await editor.PostAsJsonAsync("/admin/v1/content/entries", new
        {
            kind = "post", slug, title = "اتوماسیون دایرکت چیست؟", description = "راهنمای شروع", category = categorySlug, author = "تیم پیشخوان",
            body = Blocks("نسخه اول"), data = new { seoTitle = "اتوماسیون دایرکت", cover = "/images/cover.png" },
        }, Ct), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().ShouldBe("draft");
        created.GetProperty("body").GetArrayLength().ShouldBe(3);
        (await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "post", slug, title = "t", category = categorySlug }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Not on the site until published, but previewable with a token.
        var site = _api.CreateClient();
        var url = $"/api/v1/public/posts/{Uri.EscapeDataString(slug)}";
        (await site.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var preview = await Json(await editor.PostAsync($"/admin/v1/content/entries/{id}/preview", null, Ct), HttpStatusCode.OK);
        var token = preview.GetProperty("token").GetString()!;
        (await Json(await site.GetAsync($"/api/v1/public/preview?token={token}", Ct), HttpStatusCode.OK)).GetProperty("title").GetString().ShouldBe("اتوماسیون دایرکت چیست؟");
        (await site.GetAsync($"/api/v1/public/preview?token={token[..^4]}AAAA", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await site.GetAsync("/api/v1/public/preview?token=garbage", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Publish: the site shows it, and is asked to refresh the post, the blog and its category.
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var published = await Json(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{id}/publish", new { }, Ct), HttpStatusCode.OK);
        published.GetProperty("status").GetString().ShouldBe("published");
        published.GetProperty("hasUnpublishedChanges").GetBoolean().ShouldBeFalse();
        var live = await Json(await EventuallyAsync(url, (r, _) => r.StatusCode == HttpStatusCode.OK, "the published post"), HttpStatusCode.OK);
        live.GetProperty("body")[1].GetProperty("text").GetString().ShouldBe("نسخه اول");
        live.GetProperty("data").GetProperty("seoTitle").GetString().ShouldBe("اتوماسیون دایرکت");
        live.GetProperty("category").GetString().ShouldBe(categorySlug);
        var list = await Json(await site.GetAsync($"/api/v1/public/posts?category={categorySlug}", Ct), HttpStatusCode.OK);
        list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("slug").GetString()).ShouldBe([slug]);
        list.GetProperty("items")[0].TryGetProperty("body", out _).ShouldBeFalse();
        (await Json(await site.GetAsync("/api/v1/public/categories?kind=blog", Ct), HttpStatusCode.OK)).EnumerateArray().ShouldContain(c => c.GetProperty("slug").GetString() == categorySlug);

        var watch = Stopwatch.StartNew();
        while (!FakeWebsite.Calls.Any(c => c.At >= since && c.Body.Contains(slug, StringComparison.Ordinal)) && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, Ct);
        }

        var call = FakeWebsite.Calls.Last(c => c.At >= since && c.Body.Contains(slug, StringComparison.Ordinal));
        var body = JsonDocument.Parse(call.Body).RootElement;
        body.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["content", "content:post"]);
        body.GetProperty("paths").EnumerateArray().Select(p => p.GetString()).ShouldBe(["/blog", $"/blog/{slug}", $"/blog/category/{categorySlug}", "/sitemap.xml"]);
        call.Signature.ShouldBe(SiteSignature.Sign(FakeWebsite.Secret, long.Parse(call.Timestamp, System.Globalization.CultureInfo.InvariantCulture), call.Body));

        // Editing the draft leaves the site alone until the next publish; stale revisions and slug changes are refused.
        var edit = new { slug, title = "اتوماسیون دایرکت چیست؟", category = categorySlug, body = Blocks("نسخه دوم"), revision = 1 };
        var edited = await Json(await editor.PutAsJsonAsync($"/admin/v1/content/entries/{id}", edit, Ct), HttpStatusCode.OK);
        edited.GetProperty("hasUnpublishedChanges").GetBoolean().ShouldBeTrue();
        (await Code(await editor.PutAsJsonAsync($"/admin/v1/content/entries/{id}", edit, Ct))).ShouldBe("content.stale");
        (await Code(await editor.PutAsJsonAsync($"/admin/v1/content/entries/{id}", edit with { slug = slug + "-2", revision = 2 }, Ct))).ShouldBe("content.slug_locked");
        (await Json(await site.GetAsync(url, Ct), HttpStatusCode.OK)).GetProperty("body")[1].GetProperty("text").GetString().ShouldBe("نسخه اول");
        (await Json(await site.GetAsync($"/api/v1/public/preview?token={token}", Ct), HttpStatusCode.OK)).GetProperty("body")[1].GetProperty("text").GetString().ShouldBe("نسخه دوم");

        // Scheduled: published by the platform job once the time comes.
        (await Json(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{id}/publish", new { at = DateTimeOffset.UtcNow.AddSeconds(2) }, Ct), HttpStatusCode.OK))
            .GetProperty("scheduledAt").ValueKind.ShouldBe(JsonValueKind.String);
        await using (var scope = _admin.App.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<ScheduledPublishJob>();
            (await job.RunAsync(Ct)).ShouldBe(0);
            await Task.Delay(2_500, Ct);
            (await job.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        }

        await EventuallyAsync(url, (r, text) => r.StatusCode == HttpStatusCode.OK && text.Contains("نسخه دوم", StringComparison.Ordinal), "the scheduled edit");
        var again = await Json(await editor.GetAsync($"/admin/v1/content/entries/{id}", Ct), HttpStatusCode.OK);
        again.GetProperty("scheduledAt").ValueKind.ShouldBe(JsonValueKind.Null);
        again.GetProperty("hasUnpublishedChanges").GetBoolean().ShouldBeFalse();

        // Archiving takes it off; a published entry can't be deleted, and its category can't be either.
        (await Code(await editor.DeleteAsync($"/admin/v1/content/entries/{id}", Ct))).ShouldBe("content.was_published");
        (await Code(await editor.DeleteAsync($"/admin/v1/content/categories/{category.GetProperty("id").GetGuid()}", Ct))).ShouldBe("content.category_in_use");
        (await Json(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{id}/archive", new { reason = "قدیمی شد" }, Ct), HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("archived");
        await EventuallyAsync(url, (r, _) => r.StatusCode == HttpStatusCode.NotFound, "the archived post leaving the site");

        // Roles: support reads, only editors write.
        var support = _admin.StaffClient(StaffRoles.Support);
        (await support.GetAsync($"/admin/v1/content/entries?kind=post&q={Uri.EscapeDataString("اتوماسیون")}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await support.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "page", slug = "x", title = "t" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/content/entries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Help_search_case_study_consent_faqs_pages_and_redirects()
    {
        var editor = _admin.StaffClient(StaffRoles.ContentEditor);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var help = await Json(await editor.PostAsJsonAsync("/admin/v1/content/categories", new { kind = "help", slug = $"start-{tag}", title = "شروع کار" }, Ct), HttpStatusCode.Created);
        var helpCategory = help.GetProperty("slug").GetString()!;

        async Task<Guid> PublishAsync(object entry)
        {
            var created = await Json(await editor.PostAsJsonAsync("/admin/v1/content/entries", entry, Ct), HttpStatusCode.Created);
            var id = created.GetProperty("id").GetGuid();
            await Json(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{id}/publish", new { }, Ct), HttpStatusCode.OK);
            return id;
        }

        // A help article without its category can't be published.
        var orphan = await Json(await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "help", slug = $"orphan-{tag}", title = "t" }, Ct), HttpStatusCode.Created);
        (await Code(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{orphan.GetProperty("id").GetGuid()}/publish", new { }, Ct))).ShouldBe("content.category_required");

        // Search normalises Persian (Arabic ي/ك) and matches every word, title matches first.
        var word = "واژه" + tag;
        await PublishAsync(new { kind = "help", slug = $"connect-{tag}", title = $"اتصال اینستاگرام {word}", category = helpCategory, body = Blocks("حساب حرفه‌ای لازم است") });
        await PublishAsync(new { kind = "help", slug = $"keys-{tag}", title = "رمز عبور", category = helpCategory, body = Blocks($"درباره {word} و حساب"), data = new { keywords = new[] { "پسورد" } } });
        var results = await EventuallyAsync($"/api/v1/public/help/search?q={Uri.EscapeDataString(word + " حساب")}", (r, text) => text.Contains($"keys-{tag}", StringComparison.Ordinal), "both articles in search");
        (await Json(results, HttpStatusCode.OK)).EnumerateArray().Select(a => a.GetProperty("slug").GetString()).ShouldBe([$"connect-{tag}", $"keys-{tag}"]);
        var arabic = await Json(await _api.CreateClient().GetAsync($"/api/v1/public/help/search?q={Uri.EscapeDataString("پسورد " + word.Replace('ی', 'ي'))}", Ct), HttpStatusCode.OK);
        arabic.EnumerateArray().Select(a => a.GetProperty("slug").GetString()).ShouldBe([$"keys-{tag}"]);
        (await Json(await _api.CreateClient().GetAsync($"/api/v1/public/help?category={helpCategory}", Ct), HttpStatusCode.OK)).GetArrayLength().ShouldBe(2);

        // A case study is published only with the seller's consent recorded.
        var study = await Json(await editor.PostAsJsonAsync("/admin/v1/content/entries", new { kind = "case_study", slug = $"shop-{tag}", title = "فروشگاه نمونه", data = new { metrics = new[] { new { label = "پاسخ", value = "۹۵٪" } } } }, Ct), HttpStatusCode.Created);
        var studyId = study.GetProperty("id").GetGuid();
        (await Code(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{studyId}/publish", new { }, Ct))).ShouldBe("content.consent_required");
        await Json(await editor.PutAsJsonAsync($"/admin/v1/content/entries/{studyId}", new { slug = $"shop-{tag}", title = "فروشگاه نمونه", data = new { consent = true } }, Ct), HttpStatusCode.OK);
        await Json(await editor.PostAsJsonAsync($"/admin/v1/content/entries/{studyId}/publish", new { }, Ct), HttpStatusCode.OK);
        await EventuallyAsync("/api/v1/public/case-studies", (r, text) => text.Contains($"shop-{tag}", StringComparison.Ordinal), "the case study");

        // Pages, legal pages and FAQs by tag.
        await PublishAsync(new { kind = "page", slug = $"about-{tag}", title = "درباره ما", body = new object[] { new { type = "hero", title = "پیشخوان", cta = new { text = "شروع", href = "/waitlist" } } } });
        (await Json(await EventuallyAsync($"/api/v1/public/pages/about-{tag}", (r, _) => r.IsSuccessStatusCode, "the page"), HttpStatusCode.OK))
            .GetProperty("body")[0].GetProperty("type").GetString().ShouldBe("hero");
        await PublishAsync(new { kind = "legal", slug = $"terms-{tag}", title = "قوانین", data = new { version = "1.0" }, body = Blocks("متن قوانین") });
        await EventuallyAsync($"/api/v1/public/legal/terms-{tag}", (r, _) => r.IsSuccessStatusCode, "the legal page");
        await PublishAsync(new { kind = "faq", slug = $"q-{tag}", title = "رمز اینستاگرام لازم است؟", description = "خیر.", data = new { tags = new[] { "landing-" + tag } } });
        var faqs = await EventuallyAsync($"/api/v1/public/faqs?tag=landing-{tag}", (r, text) => text.Contains($"q-{tag}", StringComparison.Ordinal), "the FAQ");
        (await Json(faqs, HttpStatusCode.OK)).GetArrayLength().ShouldBe(1);

        // Redirects.
        (await editor.PostAsJsonAsync("/admin/v1/content/redirects", new { from = "javascript:x", to = "/a" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var redirect = await Json(await editor.PostAsJsonAsync("/admin/v1/content/redirects", new { from = $"/old-{tag}", to = $"/help/{helpCategory}" }, Ct), HttpStatusCode.Created);
        (await Code(await editor.PostAsJsonAsync("/admin/v1/content/redirects", new { from = $"/old-{tag}", to = "/" }, Ct))).ShouldBe("content.redirect_exists");
        await EventuallyAsync("/api/v1/public/redirects", (r, text) => text.Contains($"/old-{tag}", StringComparison.Ordinal), "the redirect");
        (await editor.DeleteAsync($"/admin/v1/content/redirects/{redirect.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await EventuallyAsync("/api/v1/public/redirects", (r, text) => !text.Contains($"/old-{tag}", StringComparison.Ordinal), "the redirect gone");
    }
}
