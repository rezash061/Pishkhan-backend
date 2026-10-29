using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.2 acceptance: the inbox list stays under 200 ms at p95 with 1 M messages in the workspace.</summary>
[Collection(WorkersCollection.Name)]
public sealed class InboxPerformanceTests(InfraFixture infra) : IAsyncLifetime
{
    private const int Conversations = 20_000;
    private const int MessagesPerConversation = 50;
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Inbox_list_p95_is_under_200ms_with_a_million_messages()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var account = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(infra.PostgresConnectionString))
        {
            await connection.OpenAsync(Ct);
            var seed = new CommandDefinition(
                """
                INSERT INTO conversations.conversations
                    (id, workspace_id, channel_account_id, channel_kind, contact_id, external_user_id, status, ai_paused, unread_count,
                     last_inbound_at, window_expires_at, last_message_preview, created_at, updated_at)
                SELECT gen_random_uuid(), @ws, @account, 'instagram', gen_random_uuid(), 'igsid-' || g,
                       (ARRAY['open','open','open','pending','handed_off','closed'])[1 + g % 6], false, g % 3,
                       now() - make_interval(secs => g * 60), now() - make_interval(secs => g * 60) + interval '24 hours',
                       'سلام، قیمت این مانتو چنده؟', now() - interval '20 days', now()
                FROM generate_series(1, @conversations) AS g;

                INSERT INTO conversations.messages (id, created_at, updated_at, workspace_id, conversation_id, direction, kind, text, normalized_text, status)
                SELECT gen_random_uuid(), now() - make_interval(secs => (c.n * 60) + m * 7), now(), @ws, c.id,
                       CASE WHEN m % 2 = 0 THEN 'inbound' ELSE 'outbound' END, 'text', 'سلام، قیمت این مانتو چنده؟', 'سلام قیمت این مانتو چنده', 'received'
                FROM (SELECT id, row_number() OVER () AS n FROM conversations.conversations WHERE workspace_id = @ws) c
                CROSS JOIN generate_series(1, @perConversation) AS m;

                ANALYZE conversations.conversations;
                ANALYZE conversations.messages;
                """,
                new { ws, account, conversations = Conversations, perConversation = MessagesPerConversation },
                commandTimeout: 600,
                cancellationToken: Ct);
            await connection.ExecuteAsync(seed);
            (await connection.QuerySingleAsync<long>("SELECT count(*) FROM conversations.messages WHERE workspace_id = @ws", new { ws }))
                .ShouldBe(Conversations * MessagesPerConversation);
        }

        var root = $"/api/v1/workspaces/{ws}/conversations";
        var first = await client.GetFromJsonAsync<JsonElement>(root, Ct);
        var cursor = first.GetProperty("nextCursor").GetString();
        var conversationId = first.GetProperty("items")[0].GetProperty("id").GetGuid();
        string[] urls =
        [
            root,
            $"{root}?status=open",
            $"{root}?unread=true",
            $"{root}?assignee=none",
            $"{root}?cursor={cursor}",
            $"{root}/{conversationId}/messages",
        ];

        foreach (var url in urls)
        {
            (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode();
        }

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            foreach (var url in urls)
            {
                var started = Stopwatch.GetTimestamp();
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode();
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        TestContext.Current.SendDiagnosticMessage($"inbox p50 {timings[timings.Count / 2]:F1} ms, p95 {p95:F1} ms over {timings.Count} requests");
        p95.ShouldBeLessThan(200, $"p50 {timings[timings.Count / 2]:F1} ms");
    }
}
