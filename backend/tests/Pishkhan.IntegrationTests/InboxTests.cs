using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>Inbox API and the outbound pipeline (BE-1.2): window and HUMAN_AGENT policy, read receipts, inbox actions, tenant isolation.</summary>
[Collection(WorkersCollection.Name)]
public sealed class InboxTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed record Seeded(Guid WorkspaceId, Guid AccountId, string AccountExternalId, HttpClient Client);

    private async Task<Seeded> SeedWorkspaceAsync()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1784" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var account = ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);
        return new Seeded(ws, account.Id, externalId, client);
    }

    private async Task<Guid> SeedConversationAsync(Seeded seeded, TimeSpan lastInboundAgo, string? customer = null)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(seeded.WorkspaceId);
        var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var contact = Contact.Create(seeded.WorkspaceId, null, null, now);
        var user = customer ?? "igsid-" + Guid.NewGuid().ToString("N");
        var conversation = Conversation.Open(seeded.WorkspaceId, seeded.AccountId, ChannelKinds.Instagram, contact.Id, user, now);
        conversation.RecordInbound(now - lastInboundAgo, "سلام");
        db.AddRange(contact, ChannelIdentity.Create(seeded.WorkspaceId, contact.Id, seeded.AccountId, user, now), conversation);
        await db.SaveChangesAsync(Ct);
        return conversation.Id;
    }

    private static async Task<JsonElement> SettledMessageAsync(HttpClient client, Guid ws, Guid conversationId, Guid messageId, string until = MessageStatuses.Sent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/conversations/{conversationId}/messages", Ct);
            var message = page.GetProperty("items").EnumerateArray().First(m => m.GetProperty("id").GetGuid() == messageId);
            var status = message.GetProperty("status").GetString();
            if (status == until || status == MessageStatuses.Failed)
            {
                return message;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"message {messageId} stayed {status}");
            await Task.Delay(200, Ct);
        }
    }

    private static async Task<HttpResponseMessage> ReplyAsync(Seeded s, Guid conversationId, object body) =>
        await s.Client.PostAsJsonAsync($"/api/v1/workspaces/{s.WorkspaceId}/conversations/{conversationId}/messages", body, Ct);

    [Fact]
    public async Task Replies_follow_the_window_and_human_agent_rules()
    {
        var s = await SeedWorkspaceAsync();
        var fresh = await SeedConversationAsync(s, TimeSpan.FromHours(1));
        var threeDays = await SeedConversationAsync(s, TimeSpan.FromDays(3));
        var eightDays = await SeedConversationAsync(s, TimeSpan.FromDays(8));

        var inWindow = await ReplyAsync(s, fresh, new { text = "سلام، موجود است", quickReplies = new[] { new { title = "سفارش", payload = "ORDER" } } });
        inWindow.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var sent = await SettledMessageAsync(s.Client, s.WorkspaceId, fresh, (await inWindow.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        sent.GetProperty("status").GetString().ShouldBe(MessageStatuses.Sent);
        sent.GetProperty("tag").ValueKind.ShouldBe(JsonValueKind.Null);

        var tagged = await ReplyAsync(s, threeDays, new { text = "ببخشید دیر شد، هنوز موجود است" });
        tagged.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var taggedId = (await tagged.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await SettledMessageAsync(s.Client, s.WorkspaceId, threeDays, taggedId)).GetProperty("tag").GetString().ShouldBe(MessageTags.HumanAgent);
        FakeInstagram.Sent.ShouldContain(b => b.Contains("ببخشید دیر شد", StringComparison.Ordinal) && b.Contains("\"tag\":\"HUMAN_AGENT\"", StringComparison.Ordinal));
        FakeInstagram.Sent.ShouldContain(b => b.Contains("سلام، موجود است", StringComparison.Ordinal) && b.Contains("\"quick_replies\"", StringComparison.Ordinal) && !b.Contains("HUMAN_AGENT", StringComparison.Ordinal));

        var closed = await ReplyAsync(s, eightDays, new { text = "سلام" });
        closed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await closed.Content.ReadAsStringAsync(Ct)).ShouldContain("conversation.window_closed");

        (await ReplyAsync(s, fresh, new { text = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Automated_messages_never_use_the_human_agent_tag()
    {
        var s = await SeedWorkspaceAsync();
        var conversationId = await SeedConversationAsync(s, TimeSpan.FromDays(2));
        Guid messageId;
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(s.WorkspaceId);
            var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var message = Message.QueueOutbound(s.WorkspaceId, conversationId, "یادآوری سبد خرید", HandledBy.Rule, null, DateTimeOffset.UtcNow);
            db.Messages.Add(message);
            db.Publish(new OutboundMessageRequested(conversationId, message.Id, message.CreatedAt, HandledBy.Rule, null), conversationId.ToString());
            await db.SaveChangesAsync(Ct);
            messageId = message.Id;
        }

        var settled = await SettledMessageAsync(s.Client, s.WorkspaceId, conversationId, messageId);
        settled.GetProperty("status").GetString().ShouldBe(MessageStatuses.Failed);
        settled.GetProperty("errorCode").GetString().ShouldBe("conversation.window_closed");
    }

    [Fact]
    public async Task A_read_receipt_marks_earlier_replies_read()
    {
        var s = await SeedWorkspaceAsync();
        var customer = "igsid-" + Guid.NewGuid().ToString("N");
        var conversationId = await SeedConversationAsync(s, TimeSpan.FromMinutes(5), customer);
        var first = (await (await ReplyAsync(s, conversationId, new { text = "اول" })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SettledMessageAsync(s.Client, s.WorkspaceId, conversationId, first);
        var second = (await (await ReplyAsync(s, conversationId, new { text = "دوم" })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SettledMessageAsync(s.Client, s.WorkspaceId, conversationId, second);

        string mid;
        await using (var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            mid = await connection.QuerySingleAsync<string>("SELECT external_id FROM conversations.external_message_keys WHERE message_id = @id", new { id = second });
        }

        var entry = JsonDocument.Parse($$$"""
            {"id":"{{{s.AccountExternalId}}}","time":1,"messaging":[{"sender":{"id":"{{{customer}}}"},"recipient":{"id":"{{{s.AccountExternalId}}}"},"timestamp":{{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}},"read":{"mid":"{{{mid}}}"}}]}
            """).RootElement;
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Publish(new InstagramWebhookReceived("instagram", s.AccountExternalId, entry, DateTimeOffset.UtcNow), s.AccountExternalId);
            await db.SaveChangesAsync(Ct);
        }

        var read = await SettledMessageAsync(s.Client, s.WorkspaceId, conversationId, first, MessageStatuses.Read);
        read.GetProperty("status").GetString().ShouldBe(MessageStatuses.Read);
        read.GetProperty("readAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        (await SettledMessageAsync(s.Client, s.WorkspaceId, conversationId, second, MessageStatuses.Read)).GetProperty("status").GetString().ShouldBe(MessageStatuses.Read);
    }

    [Fact]
    public async Task Inbox_actions_filters_quick_replies_and_isolation()
    {
        var s = await SeedWorkspaceAsync();
        var conversationId = await SeedConversationAsync(s, TimeSpan.FromMinutes(1));
        await SeedConversationAsync(s, TimeSpan.FromMinutes(2));
        var root = $"/api/v1/workspaces/{s.WorkspaceId}/conversations";
        var me = (await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();

        (await s.Client.PostAsJsonAsync($"{root}/{conversationId}/assign", new { assigneeId = me }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Client.PostAsJsonAsync($"{root}/{conversationId}/assign", new { assigneeId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var mine = await s.Client.GetFromJsonAsync<JsonElement>($"{root}?assignee=me", Ct);
        mine.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([conversationId]);
        (await s.Client.GetFromJsonAsync<JsonElement>($"{root}?assignee=none", Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await s.Client.GetFromJsonAsync<JsonElement>($"{root}?unread=true&limit=1", Ct)).GetProperty("nextCursor").GetString().ShouldNotBeNull();

        var handedOff = await s.Client.PostAsJsonAsync($"{root}/{conversationId}/status", new { status = "handed_off", reason = "price_negotiation" }, Ct);
        var conversation = await handedOff.Content.ReadFromJsonAsync<JsonElement>(Ct);
        conversation.GetProperty("status").GetString().ShouldBe(ConversationStatuses.HandedOff);
        conversation.GetProperty("aiPaused").GetBoolean().ShouldBeTrue();
        await using (var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            (await connection.QuerySingleAsync<int>(
                "SELECT count(*) FROM platform.outbox_messages WHERE key = @key AND type = 'conversations.handoff-requested'", new { key = conversationId.ToString() })).ShouldBe(1);
        }

        (await s.Client.PostAsJsonAsync($"{root}/{conversationId}/status", new { status = "archived" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Client.PostAsJsonAsync($"{root}/{conversationId}/ai-pause", new { paused = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Client.PostAsync($"{root}/{conversationId}/read", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Client.GetFromJsonAsync<JsonElement>($"{root}/{conversationId}", Ct)).GetProperty("unreadCount").GetInt32().ShouldBe(0);

        (await s.Client.PostAsJsonAsync($"{root}/{conversationId}/notes", new { text = "مشتری قبلی است، تخفیف بدهید" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await s.Client.GetFromJsonAsync<JsonElement>($"{root}/{conversationId}/notes", Ct)).GetArrayLength().ShouldBe(1);

        var quick = $"/api/v1/workspaces/{s.WorkspaceId}/quick-replies";
        var created = await s.Client.PostAsJsonAsync(quick, new { shortcut = "/Price", text = "قیمت این محصول ۸۹۰ هزار تومان است" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("shortcut").GetString().ShouldBe("price");
        (await s.Client.PostAsJsonAsync(quick, new { shortcut = "price", text = "تکراری" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var stranger = await _api.SignInAsync(Ct);
        (await stranger.GetAsync($"{root}/{conversationId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.PostAsJsonAsync($"{root}/{conversationId}/messages", new { text = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync(quick, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
