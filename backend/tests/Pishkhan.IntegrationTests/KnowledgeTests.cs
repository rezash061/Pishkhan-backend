using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.4 part 1: FAQ and text sources, catalog products as knowledge, Persian chunking, embeddings and hybrid search.</summary>
[Collection(WorkersCollection.Name)]
public sealed class KnowledgeTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay and the AI group that indexes.</summary>
    private readonly ChannelsHost _workers = workers;
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> IndexedAsync(HttpClient client, string url, int revision)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var source = await client.GetFromJsonAsync<JsonElement>(url, Ct);
            if (source.GetProperty("status").GetString() != "pending" && source.GetProperty("indexedRevision").ValueKind == JsonValueKind.Number
                && source.GetProperty("indexedRevision").GetInt32() >= revision)
            {
                return source;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), $"{url} is indexed");
            await Task.Delay(300, Ct);
        }
    }

    [Fact]
    public async Task Sources_are_chunked_embedded_and_found_by_hybrid_search()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var knowledge = $"/api/v1/workspaces/{ws}/knowledge";

        async Task<List<JsonElement>> SearchAsync(string query) =>
            [.. (await Json(await client.PostAsJsonAsync($"{knowledge}/search", new { query, limit = 3 }, Ct), HttpStatusCode.OK)).EnumerateArray()];

        // Validation.
        (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "url", title = "x", body = "y" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "faq", question = "سوال؟" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "product", title = "x", body = "y" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A FAQ is one chunk; a long text is split with overlap.
        var faq = await Json(await client.PostAsJsonAsync($"{knowledge}/sources", new
        {
            kind = "faq", question = "ارسال به شهرستان چند روز طول می‌کشد؟", answer = "ارسال به شهرستان‌ها با پست پیشتاز دو تا چهار روز کاری است.",
        }, Ct), HttpStatusCode.Created);
        faq.GetProperty("status").GetString().ShouldBe("pending");
        faq.GetProperty("title").GetString().ShouldBe("ارسال به شهرستان چند روز طول می‌کشد؟");
        var faqUrl = $"{knowledge}/sources/{faq.GetProperty("id").GetGuid()}";

        var policy = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"بند {i}: کالای خریداری‌شده تا هفت روز قابل مرجوع کردن است به شرط سالم بودن بسته‌بندی."));
        var text = await Json(await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "text", title = "شرایط مرجوعی", body = policy }, Ct), HttpStatusCode.Created);
        var textUrl = $"{knowledge}/sources/{text.GetProperty("id").GetGuid()}";

        (await IndexedAsync(client, faqUrl, 1)).GetProperty("chunkCount").GetInt32().ShouldBe(1);
        var indexedText = await IndexedAsync(client, textUrl, 1);
        indexedText.GetProperty("status").GetString().ShouldBe("indexed");
        indexedText.GetProperty("chunkCount").GetInt32().ShouldBeGreaterThan(1);

        // Different wording (and Arabic letters) still finds the FAQ first; the policy text answers returns.
        var shipping = await SearchAsync("ارسال شهرستان چند روزه است؟ ممنون");
        shipping[0].GetProperty("sourceId").GetGuid().ShouldBe(faq.GetProperty("id").GetGuid());
        shipping[0].GetProperty("sourceKind").GetString().ShouldBe("faq");
        shipping[0].GetProperty("text").GetString()!.ShouldContain("پست پیشتاز");
        (await SearchAsync("مرجوع كردن كالا")).First().GetProperty("sourceId").GetGuid().ShouldBe(text.GetProperty("id").GetGuid());

        // A product becomes knowledge with its price and stock, and follows the catalog.
        var product = await Json(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/catalog/products", new
        {
            title = "کیف چرم دست‌دوز",
            description = "کیف چرم طبیعی گاوی، دوخت دست",
            status = "active",
            price = new { amount = 18_500_000, currency = "IRR" },
            variants = new object[] { new { sku = "BAG-1", attributes = new Dictionary<string, string> { ["رنگ"] = "عسلی" }, stock = 2, stockPolicy = "track" } },
        }, Ct), HttpStatusCode.Created);
        var productId = product.GetProperty("id").GetGuid();
        var watch = Stopwatch.StartNew();
        JsonElement productSource;
        while (true)
        {
            var list = await client.GetFromJsonAsync<JsonElement>($"{knowledge}/sources?kind=product", Ct);
            if (list.GetProperty("items").EnumerateArray().FirstOrDefault(s => s.GetProperty("productId").ValueKind != JsonValueKind.Null) is { ValueKind: JsonValueKind.Object } found
                && found.GetProperty("status").GetString() == "indexed")
            {
                productSource = found;
                break;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "the product is indexed");
            await Task.Delay(300, Ct);
        }

        productSource.GetProperty("productId").GetGuid().ShouldBe(productId);
        productSource.GetProperty("body").GetString()!.ShouldContain("۱٬۸۵۰٬۰۰۰ تومان");
        var bag = await SearchAsync("کیف چرمی دارید؟ قیمتش چنده");
        bag[0].GetProperty("productId").GetGuid().ShouldBe(productId);
        var productUrl = $"{knowledge}/sources/{productSource.GetProperty("id").GetGuid()}";
        (await client.DeleteAsync(productUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PutAsJsonAsync(productUrl, new { body = "دست‌کاری" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An edit re-indexes; a disabled source is not retrieved; a deleted one is gone.
        var edited = await Json(await client.PutAsJsonAsync(faqUrl, new { answer = "ارسال به شهرستان‌ها با تیپاکس یک تا دو روز کاری است." }, Ct), HttpStatusCode.OK);
        edited.GetProperty("revision").GetInt32().ShouldBe(2);
        await IndexedAsync(client, faqUrl, 2);
        (await SearchAsync("ارسال به شهرستان چند روز"))[0].GetProperty("text").GetString()!.ShouldContain("تیپاکس");
        await Json(await client.PutAsJsonAsync(faqUrl, new { enabled = false }, Ct), HttpStatusCode.OK);
        (await SearchAsync("ارسال به شهرستان چند روز")).ShouldNotContain(h => h.GetProperty("sourceKind").GetString() == "faq");
        (await Json(await client.PostAsync($"{textUrl}/reindex", null, Ct), HttpStatusCode.Accepted)).GetProperty("revision").GetInt32().ShouldBe(2);
        await IndexedAsync(client, textUrl, 2);
        (await client.DeleteAsync(textUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(textUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SearchAsync("مرجوع کردن کالا")).ShouldNotContain(h => h.GetProperty("sourceKind").GetString() == "text");

        // Archiving the product removes its knowledge.
        (await client.PutAsJsonAsync($"/api/v1/workspaces/{ws}/catalog/products/{productId}", new
        {
            title = "کیف چرم دست‌دوز",
            status = "archived",
            price = new { amount = 18_500_000, currency = "IRR" },
            variants = new object[] { new { sku = "BAG-1", attributes = new Dictionary<string, string> { ["رنگ"] = "عسلی" }, stock = 2, stockPolicy = "track" } },
        }, Ct)).EnsureSuccessStatusCode();
        watch.Restart();
        while ((await client.GetAsync(productUrl, Ct)).StatusCode != HttpStatusCode.NotFound)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "the archived product leaves the knowledge");
            await Task.Delay(300, Ct);
        }

        // Another workspace finds nothing of it.
        var other = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        (await Json(await client.PostAsJsonAsync($"/api/v1/workspaces/{other}/knowledge/search", new { query = "ارسال به شهرستان" }, Ct), HttpStatusCode.OK)).GetArrayLength().ShouldBe(0);
    }

    private static async Task<JsonElement> SettledAsync(HttpClient client, string url, int revision)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var source = await client.GetFromJsonAsync<JsonElement>(url, Ct);
            if (source.GetProperty("status").GetString() != "pending" && source.GetProperty("revision").GetInt32() >= revision)
            {
                return source;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), $"{url} is settled");
            await Task.Delay(300, Ct);
        }
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid ws, string purpose, byte[] bytes, string contentType, string name)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        var id = (await Json(await client.PostAsync($"/api/v1/workspaces/{ws}/files?purpose={purpose}", form, Ct), HttpStatusCode.Accepted)).GetProperty("id").GetGuid();
        var watch = Stopwatch.StartNew();
        while ((await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/files/{id}", Ct)).GetProperty("status").GetString() != "clean")
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "file scanned");
            await Task.Delay(200, Ct);
        }

        return id;
    }

    [Fact]
    public async Task Documents_and_web_pages_become_searchable_sources()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var knowledge = $"/api/v1/workspaces/{ws}/knowledge";

        // An uploaded document: its text is extracted once it is clean, then chunked and embedded.
        var policy = "شرایط مرجوعی\nکالا تا هفت روز پس از تحویل با فاکتور قابل مرجوع است.\nهزینه ارسال مرجوعی با فروشگاه است."u8.ToArray();
        var fileId = await UploadAsync(client, ws, "knowledge_document", policy, "text/plain", "مرجوعی.txt");
        var doc = await Json(await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "file", title = "مرجوعی.txt", fileId }, Ct), HttpStatusCode.Created);
        doc.GetProperty("fileId").GetGuid().ShouldBe(fileId);
        doc = await IndexedAsync(client, $"{knowledge}/sources/{doc.GetProperty("id").GetGuid()}", 1);
        doc.GetProperty("status").GetString().ShouldBe("indexed");
        doc.GetProperty("body").GetString()!.ShouldContain("تا هفت روز پس از تحویل");
        doc.GetProperty("chunkCount").GetInt32().ShouldBeGreaterThan(0);

        var hits = await Json(await client.PostAsJsonAsync($"{knowledge}/search", new { query = "مرجوعی کالا چند روز است؟" }, Ct), HttpStatusCode.OK);
        hits.EnumerateArray().Select(h => h.GetProperty("sourceId").GetGuid()).ShouldContain(doc.GetProperty("id").GetGuid());

        // Only clean knowledge documents: an image upload is refused, and so is an unknown file.
        var image = await UploadAsync(client, ws, "product_image", Png(), "image/png", "x.png");
        (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "file", fileId = image }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "file", fileId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A web page: fetched through the SSRF-safe client; its title replaces the default one.
        var about = $"https://shop-{Guid.NewGuid():N}.example.ir/about";
        FakeWebPages.Html(about, """
            <html><head><title>درباره فروشگاه نمونه</title><script>track()</script></head>
            <body><nav>منو</nav><h1>درباره ما</h1><p>ارسال به سراسر کشور با پست پیشتاز انجام می‌شود.</p></body></html>
            """);
        var page = await Json(await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "url", url = about }, Ct), HttpStatusCode.Created);
        var pageUrl = $"{knowledge}/sources/{page.GetProperty("id").GetGuid()}";
        page = await IndexedAsync(client, pageUrl, 1);
        page.GetProperty("title").GetString().ShouldBe("درباره فروشگاه نمونه");
        page.GetProperty("body").GetString().ShouldBe("درباره ما\nارسال به سراسر کشور با پست پیشتاز انجام می‌شود.");
        page.GetProperty("url").GetString().ShouldBe(about);

        // Private addresses and plain http are refused before anything is fetched.
        foreach (var bad in new[] { "https://127.0.0.1/admin", "http://shop.example.ir/", "https://10.0.0.5/", "not a url" })
        {
            (await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "url", url = bad }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
        }

        // A page that is not there fails with the reason; once it exists, re-indexing fetches it again.
        var later = $"https://shop-{Guid.NewGuid():N}.example.ir/faq";
        var missing = await Json(await client.PostAsJsonAsync($"{knowledge}/sources", new { kind = "url", title = "سوالات", url = later }, Ct), HttpStatusCode.Created);
        var missingUrl = $"{knowledge}/sources/{missing.GetProperty("id").GetGuid()}";
        missing = await SettledAsync(client, missingUrl, 1);
        missing.GetProperty("status").GetString().ShouldBe("failed");
        missing.GetProperty("error").GetString().ShouldBe("knowledge.page_unreachable");

        FakeWebPages.Html(later, "<html><body><p>پرداخت در محل در تهران ممکن است.</p></body></html>");
        var again = await Json(await client.PostAsync($"{missingUrl}/reindex", null, Ct), HttpStatusCode.Accepted);
        missing = await IndexedAsync(client, missingUrl, again.GetProperty("revision").GetInt32());
        missing.GetProperty("title").GetString().ShouldBe("سوالات");
        missing.GetProperty("body").GetString().ShouldBe("پرداخت در محل در تهران ممکن است.");

        // Their text comes from the document or page, so it cannot be edited; the title can.
        (await client.PutAsJsonAsync(missingUrl, new { body = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Json(await client.PutAsJsonAsync(missingUrl, new { title = "پرداخت" }, Ct), HttpStatusCode.OK)).GetProperty("title").GetString().ShouldBe("پرداخت");
    }

    private static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(8, 8);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
