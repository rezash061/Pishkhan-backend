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
using Pishkhan.Modules.Channels;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>Answers Instagram token refreshes by the token in the query string: tok-ok refreshes, tok-revoked is code 190, tok-down is a 500.</summary>
internal sealed class FakeInstagram : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = HttpUtility.ParseQueryString(request.RequestUri!.Query)["access_token"] ?? string.Empty;
        var (status, body) = token switch
        {
            _ when token.StartsWith("tok-ok", StringComparison.Ordinal) => (HttpStatusCode.OK, $$"""{"access_token":"{{token}}-refreshed","token_type":"bearer","expires_in":5184000}"""),
            _ when token.StartsWith("tok-revoked", StringComparison.Ordinal) => (HttpStatusCode.BadRequest, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}"""),
            _ => (HttpStatusCode.InternalServerError, """{"error":{"message":"Please retry","code":2}}"""),
        };
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>A worker-like host: Channels and Conversations modules, the platform consumers and the outbox relay, Instagram faked.</summary>
public sealed class ChannelsHost(InfraFixture infra) : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(infra.Settings);
        builder.Configuration["Outbox:IdleDelay"] = "00:00:00.100";
        builder.AddPishkhanPlatform("it-channels");
        builder.Services.AddChannelsModule(builder.Configuration).AddConversationsModule(builder.Configuration);
        builder.Services.AddHttpClient<InstagramGraphClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeInstagram());
        builder.Services.AddPishkhanMessaging(builder.Configuration, "it-channels", m => m
            .AddConversationsPlatformConsumers()
            .AddChannelsPlatformConsumers());
        builder.Services.AddOutboxRelay(builder.Configuration);
        Host = builder.Build();
        await Host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class ChannelsTests(ChannelsHost channels) : IClassFixture<ChannelsHost>
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

        done.CompletedModules.ShouldBe(["conversations"]);
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
