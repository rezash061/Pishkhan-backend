using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.7: contacts with search and filters, custom fields, tags, segments, consents and the Excel export.</summary>
[Collection(WorkersCollection.Name)]
public sealed class CrmTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<Guid> SeedAsync(Guid ws, string? name, string? username, string? phone, string[] tags, Guid? account, TimeSpan? seenAgo)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var contact = Contact.Create(ws, name, username, now);
        foreach (var tag in tags)
        {
            contact.AddTag(tag);
        }

        if (phone is not null)
        {
            contact.SetField("phone", phone);
        }

        db.Contacts.Add(contact);
        if (account is { } accountId)
        {
            var user = "igsid-" + Guid.NewGuid().ToString("N");
            var conversation = Conversation.Open(ws, accountId, ChannelKinds.Instagram, contact.Id, user, now);
            conversation.RecordInbound(now - (seenAgo ?? TimeSpan.Zero), "سلام");
            db.AddRange(ChannelIdentity.Create(ws, contact.Id, accountId, user, now), conversation);
        }

        await db.SaveChangesAsync(Ct);
        await Task.Delay(5, Ct); // distinct creation times for the newest-first order
        return contact.Id;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static object Cond(string field, string op, object? value = null, string? key = null) => new { field, op, key, value };

    [Fact]
    public async Task Contacts_are_searched_and_edited_and_segments_tags_and_fields_work()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var sara = await SeedAsync(ws, "سارا احمدی", "sara.shop", "09121112233", ["vip"], accountA, TimeSpan.FromHours(1));
        var ali = await SeedAsync(ws, null, "ali_99", null, ["vip", "wholesale"], accountB, TimeSpan.FromDays(40));
        var reza = await SeedAsync(ws, null, null, null, [], null, null);
        var contacts = $"/api/v1/workspaces/{ws}/contacts";
        var crm = $"/api/v1/workspaces/{ws}/crm";

        async Task<List<Guid>> IdsAsync(string query = "") =>
            [.. (await client.GetFromJsonAsync<JsonElement>(contacts + query, Ct)).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];

        // List, search and filters.
        (await IdsAsync()).ShouldBe([reza, ali, sara]);
        var page = await client.GetFromJsonAsync<JsonElement>($"{contacts}?limit=2", Ct);
        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        (await IdsAsync($"?cursor={page.GetProperty("nextCursor").GetString()}")).ShouldBe([sara]);
        (await IdsAsync($"?q={Uri.EscapeDataString("سارا")}")).ShouldBe([sara]);
        (await IdsAsync($"?q={Uri.EscapeDataString("+989121112233")}")).ShouldBe([sara]);
        (await IdsAsync("?q=%40ALI")).ShouldBe([ali]);
        (await IdsAsync("?tag=vip")).ShouldBe([ali, sara]);
        (await IdsAsync($"?channelAccountId={accountA}")).ShouldBe([sara]);
        (await IdsAsync("?hasPhone=true")).ShouldBe([sara]);
        (await client.GetAsync($"{contacts}?segmentId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var detail = await client.GetFromJsonAsync<JsonElement>($"{contacts}/{sara}", Ct);
        detail.GetProperty("contact").GetProperty("channels")[0].GetGuid().ShouldBe(accountA);
        detail.GetProperty("contact").GetProperty("lastSeenAt").GetDateTimeOffset().ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(-50));
        detail.GetProperty("conversations").GetArrayLength().ShouldBe(1);
        (await client.GetAsync($"{contacts}/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Custom fields.
        await Json(await client.PostAsJsonAsync($"{crm}/fields", new { key = "city", label = "شهر", type = "select", options = new[] { "تهران", "شیراز" }, sortOrder = 1 }, Ct), HttpStatusCode.Created);
        await Json(await client.PostAsJsonAsync($"{crm}/fields", new { key = "budget", label = "بودجه", type = "number", sortOrder = 2 }, Ct), HttpStatusCode.Created);
        var birthday = await Json(await client.PostAsJsonAsync($"{crm}/fields", new { key = "birthday", label = "تولد", type = "date", sortOrder = 3 }, Ct), HttpStatusCode.Created);
        (await client.PostAsJsonAsync($"{crm}/fields", new { key = "city", label = "شهر", type = "text", sortOrder = 0 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"{crm}/fields", new { key = "Bad Key", label = "x", type = "text", sortOrder = 0 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{crm}/fields", new { key = "size", label = "سایز", type = "select", sortOrder = 0 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var fields = await client.GetFromJsonAsync<JsonElement>($"{crm}/fields", Ct);
        fields.EnumerateArray().Select(f => f.GetProperty("key").GetString()).ShouldBe(["city", "budget", "birthday"]);

        // Editing a contact: typed values are checked and normalised; null removes a value.
        var bad = await client.PatchAsJsonAsync($"{contacts}/{reza}", new { fields = new Dictionary<string, string?> { ["city"] = "اصفهان", ["birthday"] = "1369/02/11" } }, Ct);
        var badBody = await Json(bad, HttpStatusCode.BadRequest);
        badBody.GetProperty("errors").TryGetProperty("fields.city", out _).ShouldBeTrue();
        badBody.GetProperty("errors").TryGetProperty("fields.birthday", out _).ShouldBeTrue();
        var edited = await Json(await client.PatchAsJsonAsync($"{contacts}/{reza}", new
        {
            displayName = "رضا",
            tags = new[] { "lead", "lead" },
            fields = new Dictionary<string, string?> { ["city"] = "شیراز", ["budget"] = "1500000.50", ["birthday"] = "1990-05-01", ["note"] = "تماس بگیرید" },
        }, Ct), HttpStatusCode.OK);
        edited.GetProperty("displayName").GetString().ShouldBe("رضا");
        edited.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["lead"]);
        edited.GetProperty("fields").GetProperty("budget").GetString().ShouldBe("1500000.50");
        edited = await Json(await client.PatchAsJsonAsync($"{contacts}/{reza}", new { fields = new Dictionary<string, string?> { ["note"] = null } }, Ct), HttpStatusCode.OK);
        edited.GetProperty("fields").TryGetProperty("note", out _).ShouldBeFalse();
        edited.GetProperty("fields").GetProperty("city").GetString().ShouldBe("شیراز");
        (await client.DeleteAsync($"{crm}/fields/{birthday.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Segments.
        async Task<int> PreviewAsync(string match, params object[] conditions) =>
            (await Json(await client.PostAsJsonAsync($"{crm}/segments/preview", new { match, conditions }, Ct), HttpStatusCode.OK)).GetProperty("contactCount").GetInt32();

        (await PreviewAsync("all", Cond("tag", "has", "vip"), Cond("last_seen", "within_days", 30))).ShouldBe(1);
        (await PreviewAsync("any", Cond("tag", "has", "wholesale"), Cond("field", "eq", "شیراز", "city"))).ShouldBe(2);
        (await PreviewAsync("all", Cond("field", "gt", "1000000", "budget"))).ShouldBe(1);
        (await PreviewAsync("all", Cond("field", "lt", "2000-01-01", "birthday"))).ShouldBe(1);
        (await PreviewAsync("all", Cond("tag", "not_has", "vip"), Cond("created", "within_days", 1))).ShouldBe(1);
        (await PreviewAsync("all", Cond("channel_account", "eq", accountB.ToString()))).ShouldBe(1);
        (await PreviewAsync("all", Cond("username", "contains", "SARA"), Cond("has_phone", "is", true))).ShouldBe(1);
        (await PreviewAsync("all", Cond("tag", "has", "x' OR 1=1 --"))).ShouldBe(0);
        var invalid = await Json(await client.PostAsJsonAsync($"{crm}/segments/preview", new { match = "all", conditions = new[] { Cond("tag", "equals", "vip") } }, Ct), HttpStatusCode.BadRequest);
        invalid.GetProperty("errors").TryGetProperty("rule.conditions[0].op", out _).ShouldBeTrue();

        var segmentBody = new { name = "مشتریان ویژه فعال", rule = new { match = "all", conditions = new[] { Cond("tag", "has", "vip"), Cond("last_seen", "within_days", 30) } } };
        var segment = await Json(await client.PostAsJsonAsync($"{crm}/segments", segmentBody, Ct), HttpStatusCode.Created);
        segment.GetProperty("contactCount").GetInt32().ShouldBe(1);
        var segmentId = segment.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"{crm}/segments", segmentBody, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await IdsAsync($"?segmentId={segmentId}")).ShouldBe([sara]);
        var widened = await Json(await client.PutAsJsonAsync($"{crm}/segments/{segmentId}", segmentBody with { rule = new { match = "any", conditions = segmentBody.rule.conditions } }, Ct), HttpStatusCode.OK);
        widened.GetProperty("contactCount").GetInt32().ShouldBe(2);
        (await client.GetFromJsonAsync<JsonElement>($"{crm}/segments", Ct))[0].GetProperty("rule").GetProperty("match").GetString().ShouldBe("any");

        // Tags: in use without a definition, defined, renamed (merging), deleted.
        var tags = await client.GetFromJsonAsync<JsonElement>($"{crm}/tags", Ct);
        var vip = tags.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "vip");
        vip.GetProperty("defined").GetBoolean().ShouldBeFalse();
        vip.GetProperty("contactCount").GetInt32().ShouldBe(2);
        (await client.PutAsJsonAsync($"{crm}/tags/vip", new { color = "neon" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var defined = await Json(await client.PutAsJsonAsync($"{crm}/tags/vip", new { color = "red", description = "خریدار همیشگی" }, Ct), HttpStatusCode.OK);
        defined.GetProperty("defined").GetBoolean().ShouldBeTrue();
        defined.GetProperty("contactCount").GetInt32().ShouldBe(2);

        var renamed = await Json(await client.PostAsJsonAsync($"{crm}/tags/vip/rename", new { newName = "ویژه" }, Ct), HttpStatusCode.OK);
        renamed.GetProperty("color").GetString().ShouldBe("red");
        renamed.GetProperty("contactCount").GetInt32().ShouldBe(2);
        var merged = await Json(await client.PostAsJsonAsync($"{crm}/tags/wholesale/rename", new { newName = "ویژه" }, Ct), HttpStatusCode.OK);
        merged.GetProperty("contactCount").GetInt32().ShouldBe(2);
        (await client.GetFromJsonAsync<JsonElement>($"{contacts}/{ali}", Ct)).GetProperty("contact").GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["ویژه"]);
        (await client.PostAsJsonAsync($"{crm}/tags/nope/rename", new { newName = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await client.DeleteAsync($"{crm}/tags/{Uri.EscapeDataString("ویژه")}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await IdsAsync($"?tag={Uri.EscapeDataString("ویژه")}")).ShouldBeEmpty();
        (await client.DeleteAsync($"{crm}/tags/{Uri.EscapeDataString("ویژه")}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"{crm}/segments/{segmentId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Another workspace sees none of it.
        var other = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        (await client.GetAsync($"/api/v1/workspaces/{other}/contacts/{sara}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{other}/crm/tags", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Consents_are_recorded_as_history_and_contacts_export_to_excel_once()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var account = Guid.NewGuid();
        var sara = await SeedAsync(ws, "سارا", "sara", "09121112233", ["vip"], account, TimeSpan.FromHours(2));
        var ali = await SeedAsync(ws, "علی", "ali", "09351234567", ["vip"], account, null);
        await SeedAsync(ws, "مینا", null, null, [], null, null);
        var contacts = $"/api/v1/workspaces/{ws}/contacts";
        await Json(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/crm/fields", new { key = "city", label = "شهر", type = "text", sortOrder = 0 }, Ct), HttpStatusCode.Created);
        await Json(await client.PatchAsJsonAsync($"{contacts}/{sara}", new { fields = new Dictionary<string, string?> { ["city"] = "شیراز" } }, Ct), HttpStatusCode.OK);

        // Consents: the latest decision per channel counts; the history stays.
        (await client.PostAsJsonAsync($"{contacts}/{sara}/consents", new { channel = "fax", granted = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{contacts}/{Guid.NewGuid()}/consents", new { channel = "sms", granted = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Json(await client.PostAsJsonAsync($"{contacts}/{sara}/consents", new { channel = "sms", granted = true, evidence = "در دایرکت گفت پیامک بفرستید" }, Ct), HttpStatusCode.Created);
        await Json(await client.PostAsJsonAsync($"{contacts}/{ali}/consents", new { channel = "sms", granted = true }, Ct), HttpStatusCode.Created);
        var withdrawn = await Json(await client.PostAsJsonAsync($"{contacts}/{ali}/consents", new { channel = "sms", granted = false, evidence = "لغو ۱۱" }, Ct), HttpStatusCode.Created);
        withdrawn.GetProperty("current").GetProperty("sms").GetBoolean().ShouldBeFalse();
        withdrawn.GetProperty("history").GetArrayLength().ShouldBe(2);
        withdrawn.GetProperty("history")[0].GetProperty("source").GetString().ShouldBe("manual");
        var consented = (await client.GetFromJsonAsync<JsonElement>($"{contacts}?consent=sms", Ct)).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        consented.ShouldBe([sara]);
        (await client.GetAsync($"{contacts}?consent=fax", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var notGranted = await Json(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/crm/segments/preview",
            new { match = "all", conditions = new[] { Cond("tag", "has", "vip"), Cond("consent", "not_granted", "sms") } }, Ct), HttpStatusCode.OK);
        notGranted.GetProperty("contactCount").GetInt32().ShouldBe(1);

        // Export: built in the background, downloadable once, by the member who asked.
        (await client.PostAsJsonAsync($"{contacts}/exports", new { segmentId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var started = await Json(await client.PostAsJsonAsync($"{contacts}/exports", new { tag = "vip" }, Ct), HttpStatusCode.Accepted);
        started.GetProperty("status").GetString().ShouldBe("pending");
        var exportId = started.GetProperty("id").GetGuid();
        var watch = Stopwatch.StartNew();
        JsonElement export;
        while ((export = await client.GetFromJsonAsync<JsonElement>($"{contacts}/exports/{exportId}", Ct)).GetProperty("status").GetString() == "pending")
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "the export is built");
            await Task.Delay(300, Ct);
        }

        export.GetProperty("status").GetString().ShouldBe("ready");
        export.GetProperty("rowCount").GetInt32().ShouldBe(2);
        (await client.GetFromJsonAsync<JsonElement>($"{contacts}/exports", Ct))[0].GetProperty("id").GetGuid().ShouldBe(exportId);

        var teammate = await _api.SignInAsync(Ct);
        (await teammate.PostAsync($"{contacts}/exports/{exportId}/download", null, Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        var download = await Json(await client.PostAsync($"{contacts}/exports/{exportId}/download", null, Ct), HttpStatusCode.OK);
        download.GetProperty("url").GetString().ShouldNotBeNullOrEmpty();
        var again = await client.PostAsync($"{contacts}/exports/{exportId}/download", null, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("crm.export.gone");

        // The file: one row per contact, phones as text, the custom field as a column.
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        await using var stream = await storage.OpenReadAsync(Buckets.Exports, $"{ws:N}/contacts/{exportId:N}.xlsx", Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        buffer.Position = 0;
        using var workbook = new XLWorkbook(buffer);
        var sheet = workbook.Worksheet(1);
        sheet.Cell(1, 2).GetString().ShouldBe("نام");
        sheet.Cell(1, 9).GetString().ShouldBe("شهر");
        sheet.LastRowUsed()!.RowNumber().ShouldBe(3);
        sheet.Cell(2, 2).GetString().ShouldBe("سارا");
        sheet.Cell(2, 4).GetString().ShouldBe("09121112233");
        sheet.Cell(2, 6).GetString().ShouldBe("vip");
        sheet.Cell(2, 9).GetString().ShouldBe("شیراز");
        sheet.Cell(3, 2).GetString().ShouldBe("علی");
        sheet.Cell(3, 8).GetString().ShouldNotBeEmpty();
    }
}
