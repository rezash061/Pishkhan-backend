using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>What the user panel's inbox needs beyond BE-1.2: customer identity, search, handoff details, attachments; pending invitations.</summary>
[Collection(WorkersCollection.Name)]
public sealed class InboxDetailsTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<Guid> SeedAsync(Guid ws, string? name, string? username, string? phone, string? attachmentsJson = null)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var contact = Contact.Create(ws, name, username, now);
        if (phone is not null)
        {
            contact.SetField("phone", phone);
        }

        var user = "igsid-" + Guid.NewGuid().ToString("N");
        var conversation = Conversation.Open(ws, Guid.NewGuid(), ChannelKinds.Instagram, contact.Id, user, now);
        conversation.RecordInbound(now, "سلام");
        db.AddRange(contact, conversation);
        db.Messages.Add(Message.Inbound(ws, conversation.Id, attachmentsJson is null ? "text" : "story_reply", "عالیه", "عالیه", "mid-" + user, attachmentsJson, now));
        await db.SaveChangesAsync(Ct);
        return conversation.Id;
    }

    [Fact]
    public async Task Conversations_carry_the_customer_handoff_and_attachments_and_can_be_searched()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var story = JsonSerializer.Serialize(new[] { new InboundAttachment("story_reply", "https://cdn.example/story.jpg") }, EnvelopeJson.Options);
        var sara = await SeedAsync(ws, "سارا احمدی", "sara.shop", "09121112233", story);
        var ali = await SeedAsync(ws, null, "ali_99", null);
        var url = $"/api/v1/workspaces/{ws}/conversations";

        var all = await client.GetFromJsonAsync<JsonElement>(url, Ct);
        var first = all.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == sara);
        first.GetProperty("contact").GetProperty("displayName").GetString().ShouldBe("سارا احمدی");
        first.GetProperty("contact").GetProperty("username").GetString().ShouldBe("sara.shop");
        first.GetProperty("handoff").ValueKind.ShouldBe(JsonValueKind.Null);

        async Task<List<Guid>> SearchAsync(string q) =>
            [.. (await client.GetFromJsonAsync<JsonElement>($"{url}?q={Uri.EscapeDataString(q)}", Ct)).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];

        (await SearchAsync("سارا")).ShouldBe([sara]);
        (await SearchAsync("@ALI")).ShouldBe([ali]);
        (await SearchAsync("+989121112233")).ShouldBe([sara]);
        (await SearchAsync("50%_off")).ShouldBeEmpty();

        var handoff = await client.PostAsJsonAsync($"{url}/{ali}/status", new { status = "handed_off", reason = "مشتری قیمت عمده می‌خواهد" }, Ct);
        handoff.EnsureSuccessStatusCode();
        var detail = await client.GetFromJsonAsync<JsonElement>($"{url}/{ali}", Ct);
        detail.GetProperty("aiPaused").GetBoolean().ShouldBeTrue();
        detail.GetProperty("handoff").GetProperty("reason").GetString().ShouldBe("مشتری قیمت عمده می‌خواهد");
        detail.GetProperty("contact").GetProperty("username").GetString().ShouldBe("ali_99");

        (await client.PostAsJsonAsync($"{url}/{ali}/status", new { status = "open" }, Ct)).EnsureSuccessStatusCode();
        (await client.GetFromJsonAsync<JsonElement>($"{url}/{ali}", Ct)).GetProperty("handoff").ValueKind.ShouldBe(JsonValueKind.Null);

        var messages = await client.GetFromJsonAsync<JsonElement>($"{url}/{sara}/messages", Ct);
        var attachment = messages.GetProperty("items")[0].GetProperty("attachments")[0];
        attachment.GetProperty("type").GetString().ShouldBe("story_reply");
        attachment.GetProperty("url").GetString().ShouldBe("https://cdn.example/story.jpg");
    }

    [Fact]
    public async Task Pending_invitations_are_listed_masked_and_can_be_revoked()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var created = await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/invitations", new { phone = "09125556677", role = "agent" }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/invitations", Ct);
        var pending = list.EnumerateArray().Single();
        pending.GetProperty("id").GetGuid().ShouldBe(id);
        pending.GetProperty("role").GetString().ShouldBe("agent");
        pending.GetProperty("phone").GetString().ShouldBe("+98912***6677");

        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}/invitations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/invitations", Ct)).GetArrayLength().ShouldBe(0);
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}/invitations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
