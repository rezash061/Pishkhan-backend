using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations.Realtime;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.5 acceptance: two Api replicas deliver realtime events to clients connected to either (Redis backplane).</summary>
[Collection(WorkersCollection.Name)]
public sealed class RealtimeTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _replicaA = null!;
    private ApiFactory _replicaB = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _replicaA = new ApiFactory(infra);
        _replicaB = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _replicaA.DisposeAsync();
        await _replicaB.DisposeAsync();
    }

    private sealed class Inbox(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection { get; } = connection;

        public ConcurrentQueue<(string Event, JsonElement Payload)> Received { get; } = new();

        public async Task<JsonElement> WaitForAsync(string name, Func<JsonElement, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                if (Received.FirstOrDefault(r => r.Event == name && match(r.Payload)) is { Event: not null } hit)
                {
                    return hit.Payload;
                }

                DateTime.UtcNow.ShouldBeLessThan(deadline, $"no {name} event arrived");
                await Task.Delay(100, Ct);
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private static async Task<Inbox> ConnectAsync(ApiFactory replica, HttpClient signedIn)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(replica.Server.BaseAddress, InboxHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => replica.Server.CreateHandler();
                o.AccessTokenProvider = async () =>
                {
                    var response = await signedIn.PostAsync("/api/v1/auth/realtime-token", null, Ct);
                    response.EnsureSuccessStatusCode();
                    return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
                };
            })
            .Build();
        var inbox = new Inbox(connection);
        foreach (var name in new[] { InboxEvents.ConversationUpdated, InboxEvents.MessageCreated, InboxEvents.MessageStatus, InboxEvents.HandoffRequested, InboxEvents.ChannelStatus })
        {
            connection.On<JsonElement>(name, payload => inbox.Received.Enqueue((name, payload)));
        }

        await connection.StartAsync(Ct);
        return inbox;
    }

    [Fact]
    public async Task Events_reach_clients_on_either_replica()
    {
        var owner = await _replicaA.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var ownerOnB = _replicaB.CreateClient();
        ownerOnB.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        // Seed a connected Instagram account through the worker host.
        var accountExternalId = "1784" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        Guid accountId;
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-rt", TokenPurposes.Instagram);
            var account = ChannelAccount.ConnectInstagram(ws, accountExternalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), [], Guid.NewGuid(), DateTimeOffset.UtcNow);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(Ct);
            accountId = account.Id;
        }

        await using var onA = await ConnectAsync(_replicaA, owner);
        await using var onB = await ConnectAsync(_replicaB, owner);
        await onA.Connection.InvokeAsync("JoinWorkspace", ws, Ct);
        await onB.Connection.InvokeAsync("JoinWorkspace", ws, Ct);

        // A DM arrives: ingest (workers) stores it, a realtime consumer on one replica pushes, the backplane fans out.
        var customer = "igsid-" + Guid.NewGuid().ToString("N");
        var entry = JsonDocument.Parse($$$"""
            {"id":"{{{accountExternalId}}}","time":1,"messaging":[{"sender":{"id":"{{{customer}}}"},"recipient":{"id":"{{{accountExternalId}}}"},"timestamp":{{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}},"message":{"mid":"mid-{{{Guid.NewGuid():N}}}","text":"سلام، این کیف موجوده؟"}}]}
            """).RootElement;
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Publish(new InstagramWebhookReceived("instagram", accountExternalId, entry, DateTimeOffset.UtcNow), accountExternalId);
            await db.SaveChangesAsync(Ct);
        }

        static bool IsDm(JsonElement p) => p.GetProperty("lastMessage").GetString() == "سلام، این کیف موجوده؟";
        var updatedOnA = await onA.WaitForAsync(InboxEvents.ConversationUpdated, IsDm);
        await onB.WaitForAsync(InboxEvents.ConversationUpdated, IsDm);
        var conversationId = updatedOnA.GetProperty("id").GetGuid();
        updatedOnA.GetProperty("unreadCount").GetInt32().ShouldBe(1);

        // An action on replica B reaches the client connected to replica A.
        await onA.Connection.InvokeAsync("JoinConversation", ws, conversationId, Ct);
        (await ownerOnB.PostAsJsonAsync($"/api/v1/workspaces/{ws}/conversations/{conversationId}/ai-pause", new { paused = true }, Ct)).EnsureSuccessStatusCode();
        await onA.WaitForAsync(InboxEvents.ConversationUpdated, p => p.GetProperty("id").GetGuid() == conversationId && p.GetProperty("aiPaused").GetBoolean());

        // A reply sent through replica B: message.created and message.status (sent) arrive on A's conversation group.
        var reply = await ownerOnB.PostAsJsonAsync($"/api/v1/workspaces/{ws}/conversations/{conversationId}/messages", new { text = "بله موجود است" }, Ct);
        var replyId = (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await onA.WaitForAsync(InboxEvents.MessageCreated, p => p.GetProperty("message").GetProperty("id").GetGuid() == replyId);
        await onA.WaitForAsync(InboxEvents.MessageStatus, p => p.GetProperty("messageId").GetGuid() == replyId && p.GetProperty("status").GetString() == "sent");

        // Handoff from replica A reaches B.
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/conversations/{conversationId}/status", new { status = "handed_off", reason = "complaint" }, Ct)).EnsureSuccessStatusCode();
        (await onB.WaitForAsync(InboxEvents.HandoffRequested, p => p.GetProperty("conversationId").GetGuid() == conversationId)).GetProperty("reason").GetString().ShouldBe("complaint");

        // Channel status changes reach the workspace group.
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            db.Publish(new ChannelStatusChanged(accountId, ws, ChannelStatuses.ReauthRequired, "instagram.token_invalid"), accountId.ToString(), ws);
            await db.SaveChangesAsync(Ct);
        }

        await onB.WaitForAsync(InboxEvents.ChannelStatus, p => p.GetProperty("channelAccountId").GetGuid() == accountId);
    }

    [Fact]
    public async Task Joining_needs_membership_and_a_realtime_token()
    {
        var owner = await _replicaA.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var stranger = await _replicaA.SignInAsync(Ct);

        await using var strangerInbox = await ConnectAsync(_replicaA, stranger);
        var denied = await Should.ThrowAsync<HubException>(() => strangerInbox.Connection.InvokeAsync("JoinWorkspace", ws, Ct));
        denied.Message.ShouldContain("common.not_found");

        // A normal access token is not accepted by the hub.
        var withAccessToken = new HubConnectionBuilder()
            .WithUrl(new Uri(_replicaA.Server.BaseAddress, InboxHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _replicaA.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult(owner.DefaultRequestHeaders.Authorization!.Parameter);
            })
            .Build();
        await Should.ThrowAsync<HttpRequestException>(() => withAccessToken.StartAsync(Ct));
        await withAccessToken.DisposeAsync();
    }
}
