using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// Automation engine end to end (BE-1.3): webhook → ingest → rules and flows → Conversations → outbound → fake Instagram.
/// Includes the viral-post acceptance test: 2,000 comments, paced private replies, zero duplicates.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AutomationTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed record Seeded(Guid WorkspaceId, Guid AccountId, string AccountExternalId, HttpClient Client)
    {
        public string Rules => $"/api/v1/workspaces/{WorkspaceId}/automation/rules";

        public string Flows => $"/api/v1/workspaces/{WorkspaceId}/automation/flows";
    }

    /// <summary>A workspace with a connected Instagram account. <paramref name="privateRepliesPerSecond"/> sets a fast rate profile for load tests.</summary>
    private async Task<Seeded> SeedAsync(double? privateRepliesPerSecond = null, int privateReplyCapacity = 10)
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1784" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var account = ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow);
        if (privateRepliesPerSecond is { } rate)
        {
            var name = "it-" + Guid.NewGuid().ToString("N")[..12];
            var fast = new RateBucketValues(100, 100);
            db.RateProfiles.Add(new RateProfileOverride { Name = name, Values = new RateProfileValues(fast, new RateBucketValues(privateReplyCapacity, rate), fast, fast), UpdatedAt = DateTimeOffset.UtcNow });
            account.UseRateProfile(name);
        }

        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);
        return new Seeded(ws, account.Id, externalId, client);
    }

    private async Task PublishEntriesAsync(Seeded s, IEnumerable<object> entries)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var entry in entries)
        {
            var element = JsonSerializer.SerializeToElement(entry);
            db.Publish(new InstagramWebhookReceived("instagram", s.AccountExternalId, element, DateTimeOffset.UtcNow), s.AccountExternalId);
        }

        await db.SaveChangesAsync(Ct);
    }

    private Task DmAsync(Seeded s, string customer, string text, string? quickReplyPayload = null) =>
        PublishEntriesAsync(s, [new
        {
            id = s.AccountExternalId,
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            messaging = new[]
            {
                new
                {
                    sender = new { id = customer },
                    recipient = new { id = s.AccountExternalId },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    message = (object)(quickReplyPayload is null
                        ? new { mid = "m_" + Guid.NewGuid().ToString("N"), text }
                        : new { mid = "m_" + Guid.NewGuid().ToString("N"), text, quick_reply = new { payload = quickReplyPayload } }),
                },
            },
        }]);

    private static object CommentEntry(Seeded s, IEnumerable<(string Id, string From, string Text)> comments, string mediaId) => new
    {
        id = s.AccountExternalId,
        time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        changes = comments.Select(c => new
        {
            field = "comments",
            value = new { id = c.Id, text = c.Text, from = new { id = c.From, username = "user_" + c.From }, media = new { id = mediaId, media_product_type = "FEED" } },
        }).ToArray(),
    };

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return JsonDocument.Parse(content).RootElement;
    }

    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(200, Ct);
        }
    }

    private static Task Eventually(Func<bool> condition, TimeSpan timeout, string because) => Eventually(() => Task.FromResult(condition()), timeout, because);

    private static IEnumerable<JsonElement> SentTo(Func<JsonElement, bool> recipient) =>
        FakeInstagram.Sent.Select(b => JsonDocument.Parse(b).RootElement).Where(e => e.TryGetProperty("recipient", out var r) && recipient(r));

    private static IReadOnlyList<string> TextsTo(string customer) =>
        [.. SentTo(r => r.TryGetProperty("id", out var id) && id.GetString() == customer).Select(e => e.GetProperty("message").TryGetProperty("text", out var t) ? t.GetString()! : string.Empty)];

    private static string? CommentOf(JsonElement body) =>
        body.GetProperty("recipient").TryGetProperty("comment_id", out var c) ? c.GetString() : null;

    private async Task<T> ConversationsAsync<T>(Guid ws, Func<ConversationsDbContext, Task<T>> query)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        return await query(scope.ServiceProvider.GetRequiredService<ConversationsDbContext>());
    }

    private async Task<long> CountAsync(string sql, object args)
    {
        await using var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<long>(sql, args);
    }

    [Fact]
    public async Task Dm_rules_answer_keywords_and_a_flow_collects_an_answer()
    {
        var s = await SeedAsync();

        var flow = await PostAsync(s.Client, s.Flows, new
        {
            name = "ثبت سفارش",
            graph = new
            {
                start = "ask",
                nodes = new object[]
                {
                    new { id = "ask", type = "question", message = new { text = "اسم و شهرتون؟" }, answerType = "text", variable = "name" },
                    new { id = "thanks", type = "message", message = new { text = "ممنون {{flow.name}}، سفارشتون ثبت شد" } },
                    new { id = "tag", type = "action", actions = new[] { new { kind = "add_tag", tag = "ordered" } } },
                },
                edges = new[] { new { from = "ask", to = "thanks" }, new { from = "thanks", to = "tag" } },
            },
        });
        var flowId = flow.GetProperty("id").GetGuid();
        flow.GetProperty("validation").GetProperty("errors").GetArrayLength().ShouldBe(0);

        // A rule cannot start a flow that was never published.
        var order = new
        {
            name = "سفارش",
            triggerKind = "dm_keyword",
            trigger = new { match = "any_of", keywords = new[] { "سفارش", "خرید" } },
            actions = new object[] { new { kind = "start_flow", flowId }, new { kind = "add_tag", tag = "lead" } },
            priority = 0,
        };
        (await s.Client.PostAsJsonAsync(s.Rules, order, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await PostAsync(s.Client, $"{s.Flows}/{flowId}/publish", new { }, HttpStatusCode.OK);
        await PostAsync(s.Client, s.Rules, order);
        await PostAsync(s.Client, s.Rules, new
        {
            name = "قیمت",
            triggerKind = "dm_keyword",
            trigger = new { match = "exact", keywords = new[] { "قیمت" } },
            actions = new[] { new { kind = "send_message", message = new { text = "سلام {{contact.first_name|دوست عزیز}}، قیمت ۲۵۰ هزار تومان است" } } },
            priority = 0,
        });

        // Arabic yeh, typed on an Arabic keyboard layout, still matches "قیمت".
        var buyer = "c_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(s, buyer, "قيمت");
        await Eventually(() => TextsTo(buyer).Count == 1, TimeSpan.FromSeconds(30), "price reply");
        TextsTo(buyer)[0].ShouldBe("سلام دوست عزیز، قیمت ۲۵۰ هزار تومان است");

        var customer = "c_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(s, customer, "می‌خوام سفارش بدم");
        await Eventually(() => TextsTo(customer).Count == 1, TimeSpan.FromSeconds(30), "flow question");
        TextsTo(customer)[0].ShouldBe("اسم و شهرتون؟");

        await DmAsync(s, customer, "مریم از شیراز");
        await Eventually(() => TextsTo(customer).Count == 2, TimeSpan.FromSeconds(30), "flow thanks");
        TextsTo(customer)[1].ShouldBe("ممنون مریم از شیراز، سفارشتون ثبت شد");

        // Tags from the rule and from the flow's action node land on the contact; the inbound message records the rule.
        await Eventually(
            () => ConversationsAsync(s.WorkspaceId, async db => (await db.Contacts.AsNoTracking().Join(db.ChannelIdentities, c => c.Id, i => i.ContactId, (c, i) => new { c.Tags, i.ExternalUserId })
                .FirstAsync(x => x.ExternalUserId == customer, Ct)).Tags.Order().SequenceEqual(new[] { "lead", "ordered" })),
            TimeSpan.FromSeconds(15),
            "contact tags");
        var handled = await ConversationsAsync(s.WorkspaceId, db => db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirections.Inbound && m.Text == "قيمت").Select(m => new { m.HandledBy, m.AutomationRuleId }).SingleAsync(Ct));
        handled.HandledBy.ShouldBe(HandledBy.Rule);
        handled.AutomationRuleId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Fallback_message_answers_once_per_cooldown_when_nothing_matches()
    {
        var s = await SeedAsync();
        (await s.Client.PutAsJsonAsync($"/api/v1/workspaces/{s.WorkspaceId}/automation/settings", new { aiEnabled = false, fallbackText = "پیامتون رسید، به‌زودی جواب می‌دیم 🌷", fallbackCooldownMinutes = 60 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var customer = "c_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(s, customer, "سلام");
        await Eventually(() => TextsTo(customer).Count == 1, TimeSpan.FromSeconds(30), "fallback");
        await DmAsync(s, customer, "هستید؟");
        await Eventually(
            () => ConversationsAsync(s.WorkspaceId, db => db.Messages.AsNoTracking().AnyAsync(m => m.Text == "هستید؟", Ct)),
            TimeSpan.FromSeconds(30),
            "second message stored");
        await Task.Delay(2000, Ct);
        TextsTo(customer).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Comment_rule_binds_to_the_next_post_and_replies_once_privately_and_publicly()
    {
        var s = await SeedAsync(privateRepliesPerSecond: 20);
        var rule = await PostAsync(s.Client, s.Rules, new
        {
            name = "قیمت زیر پست بعدی",
            channelAccountId = s.AccountId,
            triggerKind = "comment_keyword",
            trigger = new { match = "any_of", keywords = new[] { "قیمت" }, scope = "next_post" },
            actions = new object[]
            {
                new { kind = "private_reply", message = new { text = "سلام {{comment.username}}! قیمت رو براتون فرستادیم" } },
                new { kind = "public_reply", replies = new[] { "دایرکت رو چک کنید 🌸" } },
                new { kind = "add_tag", tag = "commented" },
            },
            priority = 0,
        });
        var ruleId = rule.GetProperty("id").GetGuid();
        var media = "media_" + Guid.NewGuid().ToString("N")[..8];
        var asker = "x" + Random.Shared.Next(1_000_000, 9_999_999);
        var c1 = "c1_" + Guid.NewGuid().ToString("N")[..8];
        var c2 = "c2_" + Guid.NewGuid().ToString("N")[..8];

        // The same comment arrives twice (Meta redelivers); another comment has no keyword.
        var entry = CommentEntry(s, [(c1, asker, "قيمت چنده؟"), (c2, "y1", "عالیه 😍")], media);
        await PublishEntriesAsync(s, [entry]);
        await PublishEntriesAsync(s, [entry]);

        await Eventually(() => SentTo(r => r.TryGetProperty("comment_id", out var c) && c.GetString() == c1).Any() && FakeInstagram.PublicReplies.Any(p => p.CommentId == c1),
            TimeSpan.FromSeconds(30), "private and public reply");
        var reply = SentTo(r => r.TryGetProperty("comment_id", out var c) && c.GetString() == c1).Single();
        reply.GetProperty("message").GetProperty("text").GetString().ShouldBe($"سلام user_{asker}! قیمت رو براتون فرستادیم");

        // The rule is now bound to this post: a matching comment on another post gets nothing.
        var bound = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Rules}/{ruleId}", Ct);
        bound.GetProperty("postId").GetString().ShouldBe(media);
        var other = "c3_" + Guid.NewGuid().ToString("N")[..8];
        await PublishEntriesAsync(s, [CommentEntry(s, [(other, "z1", "قیمت؟")], "media_other")]);
        await Eventually(
            async () => await CountAsync("SELECT count(*) FROM automation.comment_events WHERE comment_id = @other", new { other }) == 1,
            TimeSpan.FromSeconds(30),
            "other comment processed");
        await Task.Delay(1500, Ct);

        SentTo(r => r.TryGetProperty("comment_id", out var c) && c.GetString() is { } id && (id == c1 || id == c2 || id == other)).Count().ShouldBe(1);
        FakeInstagram.PublicReplies.Count(p => p.CommentId == c1).ShouldBe(1);
        FakeInstagram.PublicReplies.ShouldNotContain(p => p.CommentId == c2 || p.CommentId == other);

        // The commenter became a contact with the rule's tag, and the stats count one of each reply.
        await Eventually(
            () => ConversationsAsync(s.WorkspaceId, async db => await db.Contacts.AsNoTracking().AnyAsync(c => c.Username == "user_" + asker && c.Tags.Contains("commented"), Ct)),
            TimeSpan.FromSeconds(15),
            "commenter contact");
        var stats = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Rules}/{ruleId}/stats", Ct);
        stats.GetProperty("privateReplies").GetInt32().ShouldBe(1);
        stats.GetProperty("publicReplies").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Backlog_run_answers_existing_comments_once_within_the_private_reply_age()
    {
        var s = await SeedAsync(privateRepliesPerSecond: 50);
        var media = "media_" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var recent = Enumerable.Range(0, 120).Select(i => ($"b{i}_{media}", $"f{i}", "قیمت؟", now.AddMinutes(-i))).ToList();
        var old = Enumerable.Range(0, 5).Select(i => ($"old{i}_{media}", $"o{i}", "قیمت؟", now.AddDays(-8 - i))).ToList();
        FakeInstagram.Comments[media] = [.. recent, .. old];

        // This comment came in before the rule existed; the backlog must still answer it.
        await PublishEntriesAsync(s, [CommentEntry(s, [(recent[0].Item1, recent[0].Item2, "قیمت؟")], media)]);
        await Eventually(
            async () => await CountAsync("SELECT count(*) FROM automation.comment_events WHERE comment_id = @id", new { id = recent[0].Item1 }) == 1,
            TimeSpan.FromSeconds(30),
            "early comment seen");

        var rule = await PostAsync(s.Client, s.Rules, new
        {
            name = "همه کامنت‌های پست",
            channelAccountId = s.AccountId,
            triggerKind = "comment_any",
            trigger = new { scope = "post", postId = media },
            actions = new[] { new { kind = "private_reply", message = new { text = "ممنون از کامنتتون 🌷" } } },
            priority = 0,
        });
        var ruleId = rule.GetProperty("id").GetGuid();
        await Task.Delay(2000, Ct); // the workers' rule cache hears about the new rule

        var ids = recent.Select(r => r.Item1).ToHashSet();
        int Replied() => SentTo(r => r.TryGetProperty("comment_id", out var c) && ids.Contains(c.GetString()!)).Count();
        await PostAsync(s.Client, $"{s.Rules}/{ruleId}/backlog-run", new { }, HttpStatusCode.Accepted);
        await Eventually(() => Replied() == 120, TimeSpan.FromSeconds(90), "120 backlog replies");

        var stats = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Rules}/{ruleId}/stats", Ct);
        var run = stats.GetProperty("latestBacklogRun");
        run.GetProperty("status").GetString().ShouldBe("completed");
        run.GetProperty("pages").GetInt32().ShouldBe(3);
        run.GetProperty("commentsQueued").GetInt32().ShouldBe(120);

        // A second run finds every comment answered already.
        await PostAsync(s.Client, $"{s.Rules}/{ruleId}/backlog-run", new { }, HttpStatusCode.Accepted);
        await Eventually(
            async () => (await s.Client.GetFromJsonAsync<JsonElement>($"{s.Rules}/{ruleId}/stats", Ct)).GetProperty("latestBacklogRun").GetProperty("status").GetString() == "completed",
            TimeSpan.FromSeconds(30),
            "second run");
        await Task.Delay(2000, Ct);
        Replied().ShouldBe(120);
        SentTo(r => r.TryGetProperty("comment_id", out var c) && c.GetString()!.StartsWith("old", StringComparison.Ordinal) && c.GetString()!.EndsWith(media, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>
    /// Acceptance test of BE-1.3: 2,000 comments on one post (plus 500 redelivered) get exactly one private reply each, paced within
    /// the account's rate profile. Time is compressed: the profile allows 50 private replies per second instead of Instagram's ~1.
    /// </summary>
    [Fact]
    public async Task Viral_post_is_paced_with_zero_duplicate_private_replies()
    {
        const int comments = 2000;
        const int capacity = 10;
        const double perSecond = 50;
        var s = await SeedAsync(privateRepliesPerSecond: perSecond, privateReplyCapacity: capacity);
        var media = "viral_" + Guid.NewGuid().ToString("N")[..8];
        await PostAsync(s.Client, s.Rules, new
        {
            name = "قیمت زیر پست وایرال",
            channelAccountId = s.AccountId,
            triggerKind = "comment_keyword",
            trigger = new { match = "contains", keywords = new[] { "قیمت" }, scope = "post", postId = media },
            actions = new[] { new { kind = "private_reply", message = new { text = "قیمت رو دایرکت فرستادیم 🌸" } } },
            priority = 0,
        });
        await Task.Delay(1000, Ct);

        var all = Enumerable.Range(0, comments).Select(i => ($"v{i}_{media}", $"vu{i}_{media[^4..]}", i % 2 == 0 ? "قیمت؟" : "قيمت چنده")).ToList();
        var entries = all.Chunk(50).Select(chunk => CommentEntry(s, chunk, media)).ToList();
        var watch = Stopwatch.StartNew();
        await PublishEntriesAsync(s, entries);
        await PublishEntriesAsync(s, entries.Take(10)); // Meta redelivers 500 of them

        var ids = all.Select(c => c.Item1).ToHashSet();
        List<(DateTimeOffset At, string Comment)> Replies() =>
            [.. FakeInstagram.SentLog.Select(l => (l.At, Comment: CommentOf(JsonDocument.Parse(l.Body).RootElement))).Where(x => x.Comment is not null && ids.Contains(x.Comment)).Select(x => (x.At, x.Comment!))];

        await Eventually(() => Replies().Count >= comments, TimeSpan.FromSeconds(240), $"{comments} private replies");
        var elapsed = watch.Elapsed;
        await Task.Delay(3000, Ct);

        var replies = Replies();
        replies.Count.ShouldBe(comments, "no comment may get a second private reply");
        replies.Select(r => r.Comment).Distinct().Count().ShouldBe(comments);
        (await CountAsync("SELECT count(*) FROM conversations.private_reply_keys WHERE channel_account_id = @account", new { account = s.AccountId })).ShouldBe(comments);
        (await CountAsync("SELECT count(*) FROM automation.comment_events WHERE channel_account_id = @account", new { account = s.AccountId })).ShouldBe(comments);
        (await CountAsync("SELECT count(*) FROM conversations.messages WHERE workspace_id = @ws AND status = 'failed'", new { ws = s.WorkspaceId })).ShouldBe(0);

        // Paced: never more than the bucket allows in any one second, and spread over at least comments / rate seconds.
        var times = replies.Select(r => r.At).Order().ToList();
        var maxPerSecond = 0;
        for (int i = 0, j = 0; i < times.Count; i++)
        {
            while (times[i] - times[j] >= TimeSpan.FromSeconds(1))
            {
                j++;
            }

            maxPerSecond = Math.Max(maxPerSecond, i - j + 1);
        }

        maxPerSecond.ShouldBeLessThanOrEqualTo(capacity + (int)perSecond + 5);
        (times[^1] - times[0]).ShouldBeGreaterThan(TimeSpan.FromSeconds((comments - capacity) / perSecond * 0.9));
        TestContext.Current.SendDiagnosticMessage($"Viral test: {comments} replies in {elapsed.TotalSeconds:F0}s, max {maxPerSecond}/s");
    }
}
