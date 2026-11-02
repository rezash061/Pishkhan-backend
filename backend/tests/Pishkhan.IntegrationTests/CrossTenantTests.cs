using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using SkiaSharp;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// The cross-tenant access suite (backend plan section 4, BE-1.15). It walks every route under <c>/workspaces/{workspaceId}</c> that the Api
/// host maps, so a new endpoint is covered without anyone remembering to add it:
/// <list type="bullet">
/// <item>A non-member calling any route of another workspace gets 404, whatever the method.</item>
/// <item>A member of workspace A calling a route of A with the IDs of workspace B's resources gets 404, and B's resources are unchanged.</item>
/// </list>
/// A route with a parameter this suite cannot seed fails the coverage check, so it has to be added to <see cref="SeedAsync"/>.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed partial class CrossTenantTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private const string Prefix = "/api/v1/workspaces/{workspaceId}";
    private const string SheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed record Route(string Method, string Template, bool Form)
    {
        public IReadOnlyList<string> Parameters => [.. ParameterRegex().Matches(Template).Select(m => m.Groups[1].Value).Where(p => p != "workspaceId")];

        public override string ToString() => $"{Method} {Template}";
    }

    /// <summary>Request bodies that pass validation, so a 404 comes from the ownership check and not from a 400 on an empty body.</summary>
    private static readonly Dictionary<string, object> Bodies = new(StringComparer.Ordinal)
    {
        ["POST /automation/flows/{flowId}/test"] = new { answers = Array.Empty<string>(), published = false },
        ["POST /automation/rules/{ruleId}/backlog-run"] = new { },
        ["PUT /automation/flows/{flowId}"] = Flow("x"),
        ["PUT /automation/rules/{ruleId}"] = Rule("x"),
        ["PUT /catalog/categories/{categoryId}"] = new { name = "x", parentId = (Guid?)null, sortOrder = 0 },
        ["PUT /catalog/products/{productId}"] = Product("x"),
        ["POST /catalog/products/{productId}/images"] = new { fileId = Guid.NewGuid() },
        ["PUT /catalog/products/{productId}/images/order"] = new { fileIds = Array.Empty<Guid>() },
        ["PUT /catalog/storefront-cards/{cardSetId}"] = new { name = "x", items = Array.Empty<object>() },
        ["PUT /crm/fields/{fieldId}"] = new { key = (string?)null, label = "x", type = "text", options = (string[]?)null, sortOrder = 0 },
        ["PUT /crm/segments/{segmentId}"] = Segment("x"),
        ["PUT /crm/tags/{name}"] = new { color = (string?)null, description = (string?)null },
        ["POST /crm/tags/{name}/rename"] = new { newName = "renamed" },
        ["PATCH /contacts/{contactId}"] = new { displayName = "x", clearDisplayName = false, tags = (string[]?)null, fields = (object?)null },
        ["POST /contacts/{contactId}/consents"] = new { channel = "sms", granted = true, evidence = (string?)null },
        ["POST /conversations/{conversationId}/messages"] = new { text = "x", quickReplies = (object?)null, buttons = (object?)null },
        ["POST /conversations/{conversationId}/assign"] = new { assigneeId = (Guid?)null },
        ["POST /conversations/{conversationId}/status"] = new { status = "closed", reason = (string?)null },
        ["POST /conversations/{conversationId}/ai-pause"] = new { paused = true },
        ["POST /conversations/{conversationId}/notes"] = new { text = "x" },
        ["PUT /knowledge/sources/{sourceId}"] = new { kind = "faq", title = (string?)null, question = "x?", answer = "y", body = (string?)null, enabled = true },
        ["POST /knowledge/unanswered/{questionId}/answer"] = new { answer = "x", question = (string?)null },
        ["PATCH /members/{userId}"] = new { role = "agent" },
        ["PATCH /quick-replies/{id}"] = new { shortcut = "x", text = "y" },
    };

    /// <summary>
    /// Routes keyed by a name rather than an ID, where the same key in another workspace is a different resource: <c>PUT /crm/tags/{name}</c>
    /// defines the tag in the caller's own workspace. The rest of the walk still checks that it reveals nothing of B (B's tag stays as it was).
    /// </summary>
    private static readonly HashSet<string> CreatesByKey = new(StringComparer.Ordinal) { "PUT /crm/tags/{name}" };

    [Fact]
    public async Task Every_workspace_route_hides_other_workspaces()
    {
        var routes = WorkspaceRoutes();
        routes.Count.ShouldBeGreaterThan(80, "the route walk found the workspace API");

        var b = await SignedInWorkspaceAsync();
        var seeded = await SeedAsync(b);

        // Every parameter has a seeded resource, so a new route cannot slip past the suite.
        var unseeded = routes.SelectMany(r => r.Parameters.Where(p => !seeded.ContainsKey(p)).Select(p => $"{r} needs '{p}'")).ToList();
        unseeded.ShouldBeEmpty("add the resource to SeedAsync");

        var a = await SignedInWorkspaceAsync();
        var leaks = new List<string>();

        // 1. A stranger to workspace B: every route of B is 404, whatever the method, with B's own IDs.
        foreach (var route in routes)
        {
            var response = await SendAsync(a.Client, route, b.WorkspaceId, seeded);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                leaks.Add($"non-member: {route} -> {(int)response.StatusCode} {await Snippet(response)}");
            }
        }

        // 2. A member of A, in A's routes, with B's resource IDs: 404.
        foreach (var route in routes.Where(r => r.Parameters.Count > 0 && !CreatesByKey.Contains($"{r.Method} {r.Template[Prefix.Length..]}")))
        {
            var response = await SendAsync(a.Client, route, a.WorkspaceId, seeded);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                leaks.Add($"foreign id: {route} -> {(int)response.StatusCode} {await Snippet(response)}");
            }
        }

        leaks.ShouldBeEmpty();

        // 3. Nothing of B was deleted: B still finds every resource it owns (channel media answers 503 here, as Instagram is not reachable).
        foreach (var route in routes.Where(r => r is { Method: "GET", Parameters.Count: > 0 }))
        {
            var response = await SendAsync(b.Client, route, b.WorkspaceId, seeded);
            response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound, $"{route} as B: {await Snippet(response)}");
        }

        // Account routes: another user's session cannot be revoked.
        var bSession = (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions", Ct)).EnumerateArray().First().GetProperty("id").GetGuid();
        (await a.Client.DeleteAsync($"/api/v1/me/sessions/{bSession}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions", Ct)).EnumerateArray().Any(s => s.GetProperty("id").GetGuid() == bSession).ShouldBeTrue();
    }

    private List<Route> WorkspaceRoutes()
    {
        var routes = new List<Route>();
        foreach (var endpoint in _api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var template = ConstraintRegex().Replace("/" + endpoint.RoutePattern.RawText!.TrimStart('/'), "{$1}");
            if (!template.StartsWith(Prefix + "/", StringComparison.Ordinal) && template != Prefix)
            {
                continue;
            }

            var form = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.Any(t => t.StartsWith("multipart/", StringComparison.Ordinal)) == true;
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                routes.Add(new Route(method, template, form));
            }
        }

        return [.. routes.OrderBy(r => r.Template, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)];
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Route route, Guid workspaceId, IReadOnlyDictionary<string, string> ids)
    {
        var path = route.Template.Replace("{workspaceId}", workspaceId.ToString(), StringComparison.Ordinal);
        foreach (var parameter in route.Parameters)
        {
            path = path.Replace("{" + parameter + "}", Uri.EscapeDataString(ids[parameter]), StringComparison.Ordinal);
        }

        if (route.Template.EndsWith("/files", StringComparison.Ordinal))
        {
            path += "?purpose=product_image";
        }

        using var request = new HttpRequestMessage(new HttpMethod(route.Method), path);
        if (route.Form)
        {
            var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(Png());
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "file", "x.png");
            request.Content = form;
        }
        else if (route.Method is "POST" or "PUT" or "PATCH")
        {
            var key = $"{route.Method} {route.Template[Prefix.Length..]}";
            request.Content = JsonContent.Create(Bodies.TryGetValue(key, out var body) ? body : new { });
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed record Workspace(Guid WorkspaceId, Guid UserId, HttpClient Client);

    private async Task<Workspace> SignedInWorkspaceAsync()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct);
        return new Workspace(ws, me.GetProperty("id").GetGuid(), client);
    }

    /// <summary>One resource of every kind in workspace B, by route parameter name.</summary>
    private async Task<Dictionary<string, string>> SeedAsync(Workspace b)
    {
        var url = $"/api/v1/workspaces/{b.WorkspaceId}";
        var ids = new Dictionary<string, string>(StringComparer.Ordinal) { ["userId"] = b.UserId.ToString() };
        var now = DateTimeOffset.UtcNow;

        async Task<string> Created(string path, object body, HttpStatusCode expected = HttpStatusCode.Created)
        {
            var response = await b.Client.PostAsJsonAsync(url + path, body, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(expected, $"{path}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("id").GetString()!;
        }

        // Channel, contact (with a tag) and conversation, in the database as a webhook would leave them.
        Guid accountId, conversationId;
        await using (var scope = _api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(b.WorkspaceId);
            var channels = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var token = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
            var account = ChannelAccount.ConnectInstagram(
                b.WorkspaceId, "1784" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999), "shop_b", "Shop B", token, now.AddDays(60), ["instagram_business_basic"], b.UserId, now);
            channels.Accounts.Add(account);
            await channels.SaveChangesAsync(Ct);
            accountId = account.Id;

            var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var contact = Contact.Create(b.WorkspaceId, "مشتری ب", "customer_b", now);
            contact.AddTag("tenant-b");
            var igsid = "igsid-" + Guid.NewGuid().ToString("N");
            var conversation = Conversation.Open(b.WorkspaceId, accountId, ChannelKinds.Instagram, contact.Id, igsid, now);
            conversation.RecordInbound(now, "سلام");
            db.AddRange(contact, ChannelIdentity.Create(b.WorkspaceId, contact.Id, accountId, igsid, now), conversation);
            await db.SaveChangesAsync(Ct);
            conversationId = conversation.Id;
            ids["contactId"] = contact.Id.ToString();
            ids["name"] = "tenant-b";

            // What the reply pipeline, the knowledge gaps and the notification writer leave behind.
            var agent = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            var trace = new AiTrace
            {
                Id = Guid.CreateVersion7(), WorkspaceId = b.WorkspaceId, ConversationId = conversationId, MessageId = Guid.CreateVersion7(),
                Input = "سلام", Outcome = "replied", CreatedAt = now,
            };
            agent.Traces.Add(trace);
            await agent.SaveChangesAsync(Ct);
            ids["traceId"] = trace.Id.ToString();

            var knowledge = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            var question = UnansweredQuestion.Create(b.WorkspaceId, "ساعت کاری؟", "ساعت کاری", conversationId, now);
            knowledge.Unanswered.Add(question);
            await knowledge.SaveChangesAsync(Ct);
            ids["questionId"] = question.Id.ToString();

            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var notification = Notification.Create(b.WorkspaceId, b.UserId, "handoff", "گفتگو منتظر شماست", null, null, now);
            notifications.Notifications.Add(notification);
            await notifications.SaveChangesAsync(Ct);
            ids["notificationId"] = notification.Id.ToString();

            // A checkout as the seller's own purchase leaves it (the plan only names what was bought).
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var plan = Plan.Create("tenant-b", new PlanDraft("ب", null, null, 1_000_000, null, null, new Dictionary<string, int?>(), [], true, false, false, 0), b.UserId, now);
            var checkout = Checkout.Create(
                b.WorkspaceId, new CheckoutQuote(CheckoutKinds.New, BillingCycles.Monthly, 1_000_000, 0, 1_000_000, now, now.AddDays(30), null), plan, b.UserId, now);
            billing.Checkouts.Add(checkout);
            await billing.SaveChangesAsync(Ct);
            ids["checkoutId"] = checkout.Id.ToString();
        }

        ids["channelId"] = accountId.ToString();
        ids["conversationId"] = conversationId.ToString();

        // Through the API, as the owner of B.
        ids["fileId"] = await UploadAsync(b, "product_image", Png(), "image/png", "b.png");
        ids["categoryId"] = await Created("/catalog/categories", new { name = "دسته ب", parentId = (Guid?)null, sortOrder = 0 });
        ids["productId"] = await Created("/catalog/products", Product("محصول ب") with { ImageFileIds = [Guid.Parse(ids["fileId"])] });
        ids["cardSetId"] = await Created("/catalog/storefront-cards", new { name = "کارت ب", items = new[] { new { productId = Guid.Parse(ids["productId"]) } } });
        ids["importId"] = await Created("/catalog/import", new { fileId = await UploadAsync(b, "import_sheet", Sheet(), SheetType, "b.xlsx") }, HttpStatusCode.Accepted);
        ids["flowId"] = await Created("/automation/flows", Flow("فرم ب"));
        ids["ruleId"] = await Created("/automation/rules", Rule("قانون ب"));
        ids["fieldId"] = await Created("/crm/fields", new { key = "city", label = "شهر", type = "text", options = (string[]?)null, sortOrder = 0 });
        ids["segmentId"] = await Created("/crm/segments", Segment("بخش ب"));
        ids["exportId"] = await Created("/contacts/exports", new { }, HttpStatusCode.Accepted);
        ids["sourceId"] = await Created("/knowledge/sources", new { kind = "faq", question = "ارسال چند روز است؟", answer = "دو تا چهار روز کاری." });
        ids["id"] = await Created("/quick-replies", new { shortcut = "salam", text = "سلام، خوش آمدید" });
        ids["orderId"] = await Created("/orders", new { conversationId = Guid.Parse(ids["conversationId"]), items = new[] { new { productId = Guid.Parse(ids["productId"]), quantity = 1 } } });
        (await b.Client.PutAsJsonAsync(url + "/payments/merchant-account", new { merchantId = Guid.NewGuid().ToString() }, Ct)).EnsureSuccessStatusCode();
        var payable = await Created("/orders", new
        {
            items = new[] { new { productId = Guid.Parse(ids["productId"]), quantity = 1 } },
            customerName = "مشتری ب",
            phone = "09123456789",
            address = new { text = "شیراز، خیابان زند، کوچه ۱۲، پلاک ۴ کد پستی ۷۱۳۴۸۹۵۶۴۱" },
        });
        ids["linkId"] = await Created($"/orders/{payable}/payment-links", new { });
        ids["batchId"] = await Created("/catalog/price-changes", new { scope = new { productIds = new[] { Guid.Parse(ids["productId"]) } }, kind = "percent", percent = 10 });
        ids["invitationId"] = await Created("/invitations", new { phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999), role = "agent" });
        return ids;
    }

    private static object Flow(string name) => new
    {
        name,
        graph = new
        {
            start = "hi",
            nodes = new object[] { new { id = "hi", type = "message", message = new { text = "سلام" } } },
            edges = Array.Empty<object>(),
        },
    };

    private static object Rule(string name) => new
    {
        name,
        channelAccountId = (Guid?)null,
        triggerKind = "dm_keyword",
        trigger = new { match = "exact", keywords = new[] { "قیمت" } },
        actions = new[] { new { kind = "send_message", message = new { text = "۲۵۰ هزار تومان" } } },
        priority = 0,
        enabled = true,
    };

    private sealed record ProductBody(string Title, Guid[]? ImageFileIds)
    {
        public string? Description => null;

        public Guid? CategoryId => null;

        public string[]? Tags => null;

        public string Status => "active";

        public object Price => new { amount = 250_000, currency = "IRR" };

        public string? LinkUrl => null;

        public object[]? Variants => null;
    }

    private static ProductBody Product(string title) => new(title, null);

    private static object Segment(string name) => new
    {
        name,
        rule = new { match = "all", conditions = new[] { new { field = "tag", op = "has", key = (string?)null, value = (object)"tenant-b" } } },
    };

    private async Task<string> UploadAsync(Workspace w, string purpose, byte[] bytes, string contentType, string name)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        var response = await w.Client.PostAsync($"/api/v1/workspaces/{w.WorkspaceId}/files?purpose={purpose}", form, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await w.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{w.WorkspaceId}/files/{id}", Ct)).GetProperty("status").GetString() != "clean")
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "file scanned");
            await Task.Delay(200, Ct);
        }

        _ = workers; // the scan runs in the workers host
        return id;
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Sheet()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("products").Cell(1, 1).Value = "title";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static async Task<string> Snippet(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return text.Length <= 200 ? text : text[..200];
    }

    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex ConstraintRegex();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex ParameterRegex();
}
