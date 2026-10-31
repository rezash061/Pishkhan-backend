using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Pishkhan.Infrastructure.Hosting;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Infrastructure.Antivirus;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Analytics;
using Pishkhan.Modules.Audit;
using Pishkhan.Modules.Automation;
using Pishkhan.Modules.Catalog;
using Pishkhan.Modules.Billing;
using Pishkhan.Modules.Channels;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Files;
using Pishkhan.Modules.Notifications;
using Pishkhan.Modules.Identity;
using Pishkhan.Modules.Tenancy;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// Fake Graph API. Sends (<c>POST /me/messages</c>) succeed and are recorded; token refreshes answer by the token in the query string:
/// tok-ok refreshes, tok-revoked is code 190, tok-down is a 500.
/// </summary>
internal sealed class FakeInstagram : HttpMessageHandler
{
    public static ConcurrentQueue<string> Sent { get; } = new();

    /// <summary>Every send with the time it reached "Instagram", for pacing checks.</summary>
    public static ConcurrentQueue<(DateTimeOffset At, string Body)> SentLog { get; } = new();

    /// <summary>Public comment replies: (comment id, text).</summary>
    public static ConcurrentQueue<(string CommentId, string Text)> PublicReplies { get; } = new();

    /// <summary>Existing comments per media id, served newest first by <c>GET /{media}/comments</c> in pages of 50.</summary>
    public static ConcurrentDictionary<string, IReadOnlyList<(string Id, string From, string Text, DateTimeOffset At)>> Comments { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/me/messages", StringComparison.Ordinal))
        {
            var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            Sent.Enqueue(sent);
            SentLog.Enqueue((DateTimeOffset.UtcNow, sent));
            return Json(HttpStatusCode.OK, $$"""{"recipient_id":"x","message_id":"mid.{{Guid.NewGuid():N}}"}""");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == HttpMethod.Post && segments is [_, var commentId, "replies"])
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            PublicReplies.Enqueue((commentId, form["message"] ?? string.Empty));
            return Json(HttpStatusCode.OK, $$"""{"id":"reply_{{Guid.NewGuid():N}}"}""");
        }

        if (request.Method == HttpMethod.Get && segments is [_, var mediaId, "comments"])
        {
            var all = Comments.GetValueOrDefault(mediaId) ?? [];
            var after = int.TryParse(HttpUtility.ParseQueryString(request.RequestUri.Query)["after"], out var a) ? a : 0;
            var page = all.Skip(after).Take(50).Select(c => $$$"""{"id":"{{{c.Id}}}","text":"{{{c.Text}}}","timestamp":"{{{c.At:yyyy-MM-ddTHH:mm:ssK}}}","from":{"id":"{{{c.From}}}","username":"u{{{c.From}}}"}}""");
            var next = after + 50 < all.Count ? $$""","paging":{"cursors":{"after":"{{after + 50}}"},"next":"https://graph/next"}""" : string.Empty;
            return Json(HttpStatusCode.OK, $$$"""{"data":[{{{string.Join(',', page)}}}]{{{next}}}}""");
        }

        if (request.Method == HttpMethod.Get && segments is [_, var id] && id != "me" && (request.RequestUri.Query.Contains("media_type", StringComparison.Ordinal)))
        {
            // Every post is "just published", so next-post rules bind to it.
            return Json(HttpStatusCode.OK, $$"""{"id":"{{id}}","media_type":"IMAGE","timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssK}}"}""");
        }

        var token = HttpUtility.ParseQueryString(request.RequestUri!.Query)["access_token"] ?? string.Empty;
        var (status, body) = token switch
        {
            _ when token.StartsWith("tok-ok", StringComparison.Ordinal) => (HttpStatusCode.OK, $$"""{"access_token":"{{token}}-refreshed","token_type":"bearer","expires_in":5184000}"""),
            _ when token.StartsWith("tok-revoked", StringComparison.Ordinal) => (HttpStatusCode.BadRequest, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}"""),
            _ => (HttpStatusCode.InternalServerError, """{"error":{"message":"Please retry","code":2}}"""),
        };
        return Json(status, body);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Every upload is clean: there is no clamd in the test containers.</summary>
internal sealed class CleanScanner : IAntivirusScanner
{
    public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) => Task.FromResult(new ScanResult(ScanVerdict.Clean, null, "fake"));

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake");

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}

/// <summary>
/// A worker-like host: Channels, Conversations, Automation, Files, Catalog, Identity and Audit modules, their consumers, the outbox relay and timers,
/// Instagram and the virus scanner faked.
/// </summary>
public sealed class ChannelsHost(InfraFixture infra) : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        // A separate source: values set through the indexer are lost when AddPishkhanPlatform inserts its json sources.
        builder.Configuration.AddInMemoryCollection(infra.Settings);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Outbox:IdleDelay"] = "00:00:00.100",
            ["Spike:EchoReplies"] = "false",
            ["Timers:IdleDelay"] = "00:00:00.100",
        });
        builder.AddPishkhanPlatform("it-channels");
        builder.Services.AddTenancyModule(builder.Configuration).AddChannelsModule(builder.Configuration).AddConversationsModule(builder.Configuration)
            .AddAutomationModule(builder.Configuration).AddFilesModule(builder.Configuration).AddCatalogModule(builder.Configuration)
            .AddIdentityModule(builder.Configuration).AddAuditModule(builder.Configuration).AddAnalyticsModule(builder.Configuration)
            .AddNotificationsModule(builder.Configuration).AddBillingModule(builder.Configuration);
        builder.Services.AddSingleton<IAntivirusScanner, CleanScanner>();
        builder.Services.AddHttpClient<InstagramGraphClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeInstagram());
        builder.Services.AddPishkhanMessaging(builder.Configuration, "it-channels", m => m
            .AddCacheInvalidation()
            .AddConversationsIngestConsumers()
            .AddConversationsEngineConsumers()
            .AddConversationsOutboundConsumers()
            .AddConversationsPlatformConsumers()
            .AddChannelsPlatformConsumers()
            .AddAutomationEngineConsumers()
            .AddAutomationOutboundConsumers()
            .AddAutomationPlatformConsumers()
            .AddFilesConsumers()
            .AddTenancyPlatformConsumers()
            .AddCatalogConsumers()
            .AddAuditConsumers()
            .AddNotificationsConsumers()
            .AddAnalyticsConsumers()
            .AddBillingConsumers());
        builder.Services.AddOutboxRelay(builder.Configuration);
        builder.Services.AddHostedService<TimerDispatcher>();
        Host = builder.Build();
        await Host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class WorkersCollection : ICollectionFixture<ChannelsHost>
{
    public const string Name = "workers";
}

[Collection(WorkersCollection.Name)]
public sealed class ChannelsTests(ChannelsHost channels)
{
    private IServiceProvider Services => channels.Host.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ChannelAccount> SeedAccountAsync(Guid workspaceId, string token, DateTimeOffset connectedAt, TimeSpan lifetime)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(token, TokenPurposes.Instagram);
        var account = ChannelAccount.ConnectInstagram(workspaceId, "1784" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999), "shop", "Shop", enc, connectedAt.Add(lifetime), ["instagram_business_basic"], Guid.NewGuid(), connectedAt);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);
        return account;
    }

    private async Task<ChannelAccount> ReloadAsync(Guid id)
    {
        await using var scope = Services.CreateAsyncScope();
        using var system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
        return await scope.ServiceProvider.GetRequiredService<ChannelsDbContext>().Accounts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }

    private async Task<IReadOnlyList<string>> StatusEventsAsync(Guid accountId)
    {
        await using var connection = await Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        var rows = await connection.QueryAsync<string>(
            "SELECT envelope::text FROM platform.outbox_messages WHERE key = @key AND type = 'channels.channel-status-changed' ORDER BY id",
            new { key = accountId.ToString() });
        return [.. rows];
    }

    [Fact]
    public async Task Token_refresh_renews_revokes_and_warns_by_outcome()
    {
        var ws = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var ok = await SeedAccountAsync(ws, "tok-ok-" + Guid.NewGuid(), now.AddDays(-55), TimeSpan.FromDays(60));
        var revoked = await SeedAccountAsync(ws, "tok-revoked-" + Guid.NewGuid(), now.AddDays(-55), TimeSpan.FromDays(60));
        var flaky = await SeedAccountAsync(ws, "tok-down-" + Guid.NewGuid(), now.AddDays(-59), TimeSpan.FromDays(60).Add(TimeSpan.FromHours(12)));
        var notDue = await SeedAccountAsync(ws, "tok-ok-" + Guid.NewGuid(), now.AddDays(-30), TimeSpan.FromDays(60));

        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<InstagramTokenRefresher>().RunAsync(Ct);
        }

        var renewed = await ReloadAsync(ok.Id);
        renewed.Status.ShouldBe(ChannelStatuses.Active);
        renewed.TokenExpiresAt!.Value.ShouldBeGreaterThan(now.AddDays(59));
        Services.GetRequiredService<ISecretProtector>().Unprotect(renewed.TokenEnc!, TokenPurposes.Instagram).ShouldEndWith("-refreshed");

        var reauth = await ReloadAsync(revoked.Id);
        reauth.Status.ShouldBe(ChannelStatuses.ReauthRequired);
        reauth.StatusReason.ShouldBe("instagram.token_invalid");
        (await StatusEventsAsync(revoked.Id)).ShouldContain(e => e.Contains("reauth_required", StringComparison.Ordinal));

        var expiring = await ReloadAsync(flaky.Id);
        expiring.Status.ShouldBe(ChannelStatuses.TokenExpiring);
        expiring.RefreshFailures.ShouldBe(1);
        (await StatusEventsAsync(flaky.Id)).ShouldContain(e => e.Contains("token_expiring", StringComparison.Ordinal));

        (await ReloadAsync(notDue.Id)).TokenEnc.ShouldBe(notDue.TokenEnc);
        (await StatusEventsAsync(ok.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Data_deletion_removes_the_customer_everywhere_and_completes_the_request()
    {
        var ws = Guid.NewGuid();
        var account = await SeedAccountAsync(ws, "tok-ok-" + Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromDays(60));
        var customer = "igsid-" + Guid.NewGuid().ToString("N");
        var other = "igsid-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await using (var scope = Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
            var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            foreach (var user in new[] { customer, other })
            {
                var contact = Contact.Create(ws, null, null, now);
                var conversation = Conversation.Open(ws, account.Id, ChannelKinds.Instagram, contact.Id, user, now);
                var message = Message.Inbound(ws, conversation.Id, "text", "سلام", "سلام", "mid-" + user, null, now);
                db.AddRange(contact, ChannelIdentity.Create(ws, contact.Id, account.Id, user, now), conversation, message);
                db.ExternalMessageKeys.Add(new ExternalMessageKey { ChannelAccountId = account.Id, ExternalId = "mid-" + user, MessageId = message.Id, CreatedAt = now });
            }

            await db.SaveChangesAsync(Ct);
        }

        Guid requestId;
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var request = new DataDeletionRequest { Id = Guid.CreateVersion7(), ConfirmationCode = Guid.NewGuid().ToString("N"), ExternalUserId = customer, CreatedAt = now, UpdatedAt = now };
            db.DataDeletionRequests.Add(request);
            db.Publish(new DataDeletionRequested(request.Id, ChannelKinds.Instagram, customer), request.Id.ToString());
            await db.SaveChangesAsync(Ct);
            requestId = request.Id;
        }

        var until = DateTime.UtcNow.AddSeconds(90);
        DataDeletionRequest? done = null;
        while (done?.Status != DataDeletionStatuses.Completed)
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the deletion request did not complete");
            await Task.Delay(250, Ct);
            await using var scope = Services.CreateAsyncScope();
            done = await scope.ServiceProvider.GetRequiredService<ChannelsDbContext>().DataDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == requestId, Ct);
        }

        done.CompletedModules.ShouldBe(["conversations", "automation"], ignoreOrder: true);
        done.ItemsDeleted.ShouldBe(5); // key, message, conversation, identity, contact

        await using (var scope = Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
            var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            (await db.Conversations.Select(c => c.ExternalUserId).ToListAsync(Ct)).ShouldBe([other]);
            (await db.ChannelIdentities.CountAsync(Ct)).ShouldBe(1);
            (await db.Contacts.CountAsync(Ct)).ShouldBe(1);
            (await db.Messages.CountAsync(Ct)).ShouldBe(1);
        }
    }
}
