using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Knowledge.Persistence;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-1.4 part 2, end to end: DM → automation (AI on, no rule) → reply pipeline → Conversations → fake Instagram. The fake models are
/// scripted: the classifier reads keywords, the writer answers from the facts or calls the catalog tool, or misbehaves on purpose.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AgentRepliesTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private const string Shipping = "ارسال به شهرستان‌ها با پست پیشتاز دو تا چهار روز کاری است.";
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeChatScript Script => workers.Host.Services.GetRequiredService<FakeChatScript>();

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        Script.Respond = Respond;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Script.Respond = null;
        await _api.DisposeAsync();
    }

    /// <summary>The scripted models. Returns null for anything else (default fake behaviour).</summary>
    private static ChatResponse? Respond(string model, IList<ChatMessage> messages, ChatOptions? options)
    {
        var user = messages.Last(m => m.Role == ChatRole.User).Text;
        if (model == "fake-classifier")
        {
            var (intent, sentiment, confidence) =
                user.Contains("قیمت", StringComparison.Ordinal) || user.Contains("گرون", StringComparison.Ordinal) ? ("price_question", "neutral", 0.9)
                : user.Contains("خراب", StringComparison.Ordinal) ? ("complaint", "negative", 0.9)
                : user.Contains("ارسال", StringComparison.Ordinal) ? ("shipping", "neutral", 0.9)
                : ("other", "neutral", 0.4);
            return Text(JsonSerializer.Serialize(new { intent, language = "fa", sentiment, confidence }));
        }

        if (model != "fake-writer")
        {
            return null;
        }

        var system = messages[0].Text;
        var toolResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (user.StartsWith("[system]", StringComparison.Ordinal))
        {
            // The guard asked for another answer: the misbehaving case repeats the made-up price.
            return Reply("باشه، برای شما ۹۹۰ هزار تومان حساب می‌کنیم", 0.9);
        }

        if (user.Contains("گرون", StringComparison.Ordinal))
        {
            return Reply("برای شما ۹۹۰ هزار تومان حساب می‌کنیم 😊", 0.9);
        }

        if (user.Contains("قیمت", StringComparison.Ordinal))
        {
            if (toolResult is null)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "search_products", new Dictionary<string, object?> { ["query"] = "کیف چرم" })]));
            }

            var product = JsonDocument.Parse(toolResult.Result!.ToString()!).RootElement.GetProperty("products")[0];
            return Reply($"قیمت {product.GetProperty("title").GetString()} {product.GetProperty("price").GetString()} است و موجود داریم 🌷", 0.9);
        }

        if (user.Contains("ارسال", StringComparison.Ordinal) && system.Contains("پست پیشتاز", StringComparison.Ordinal))
        {
            return Reply(Shipping, 0.9);
        }

        if (user.Contains("عجیب", StringComparison.Ordinal))
        {
            return Reply("فکر کنم فردا برسه", 0.2);
        }

        return Text("""{"reply":"","confidence":0.1,"answered":false}""");
    }

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse Reply(string reply, double confidence) => Text(JsonSerializer.Serialize(new { reply, confidence, answered = true }));

    private sealed record Seeded(Guid WorkspaceId, string AccountExternalId, HttpClient Client);

    private async Task<Seeded> SeedAsync()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
            var externalId = "1785" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
            db.Accounts.Add(ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
            (await client.PutAsJsonAsync($"/api/v1/workspaces/{ws}/automation/settings", new { aiEnabled = true }, Ct)).EnsureSuccessStatusCode();
            return new Seeded(ws, externalId, client);
        }
    }

    private async Task DmAsync(Seeded s, string customer, string text)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entry = JsonSerializer.SerializeToElement(new
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
                    message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text },
                },
            },
        });
        db.Publish(new InstagramWebhookReceived("instagram", s.AccountExternalId, entry, DateTimeOffset.UtcNow), s.AccountExternalId);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, string because)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(250, Ct);
        }
    }

    private static async Task<T> EventuallyValue<T>(Func<Task<T?>> probe, string because)
        where T : struct
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(250, Ct);
        }
    }

    private static IReadOnlyList<string> TextsTo(string customer) =>
        [.. FakeInstagram.Sent.Select(b => JsonDocument.Parse(b).RootElement)
            .Where(e => e.TryGetProperty("recipient", out var r) && r.TryGetProperty("id", out var id) && id.GetString() == customer)
            .Select(e => e.GetProperty("message").TryGetProperty("text", out var t) ? t.GetString()! : string.Empty)];

    /// <summary>The trace of the customer's latest turn, once written.</summary>
    private static Task<JsonElement> TraceAsync(Seeded s, Guid conversationId, int count = 1) => EventuallyValue<JsonElement>(async () =>
    {
        var page = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces?conversationId={conversationId}", Ct);
        var items = page.GetProperty("items");
        if (items.GetArrayLength() < count)
        {
            return null;
        }

        var id = items[0].GetProperty("id").GetGuid();
        return await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces/{id}", Ct);
    }, "the trace");

    private async Task<(Guid Id, string Status, string? Suggested, string? Reason)> ConversationAsync(Seeded s, string customer)
    {
        return await EventuallyValue<(Guid Id, string Status, string? Suggested, string? Reason)>(async () =>
        {
            await using var scope = workers.Host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(s.WorkspaceId);
            var db = scope.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.ExternalUserId == customer, Ct);
            return c is null ? null : (c.Id, c.Status, c.HandoffSuggestedReply, c.HandoffReason);
        }, "the conversation");
    }

    private async Task<(Seeded S, Guid ProductId)> ShopAsync()
    {
        var s = await SeedAsync();
        var faq = await s.Client.PostAsJsonAsync($"/api/v1/workspaces/{s.WorkspaceId}/knowledge/sources", new { kind = "faq", question = "ارسال به شهرستان چند روز طول می‌کشد؟", answer = Shipping }, Ct);
        faq.StatusCode.ShouldBe(HttpStatusCode.Created);
        var product = await s.Client.PostAsJsonAsync($"/api/v1/workspaces/{s.WorkspaceId}/catalog/products", new
        {
            title = "کیف چرم دست‌دوز",
            description = "کیف چرم طبیعی",
            status = "active",
            price = new { amount = 18_500_000, currency = "IRR" },
            variants = new object[] { new { sku = "BAG-1", attributes = new Dictionary<string, string> { ["رنگ"] = "عسلی" }, stock = 2, stockPolicy = "track" } },
        }, Ct);
        product.StatusCode.ShouldBe(HttpStatusCode.Created);
        var productId = (await product.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        // Wait until the FAQ is retrievable.
        await Eventually<object>(async () =>
        {
            var hits = await (await s.Client.PostAsJsonAsync($"/api/v1/workspaces/{s.WorkspaceId}/knowledge/search", new { query = "ارسال شهرستان" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            return hits.EnumerateArray().Any(h => h.GetProperty("sourceKind").GetString() == "faq") ? new object() : null;
        }, "the FAQ is indexed");
        return (s, productId);
    }

    [Fact]
    public async Task Answers_from_knowledge_and_tools_and_records_traces_and_usage()
    {
        var (s, _) = await ShopAsync();

        // A FAQ answer.
        var c1 = "c1" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c1, "سلام، ارسال به شیراز چند روزه؟");
        await Eventually(() => Task.FromResult(TextsTo(c1).Contains(Shipping) ? new object() : null), "the FAQ answer");
        var conversation = await ConversationAsync(s, c1);
        var trace = await TraceAsync(s, conversation.Id);
        trace.GetProperty("outcome").GetString().ShouldBe("sent");
        trace.GetProperty("intent").GetString().ShouldBe("shipping");
        trace.GetProperty("facts").EnumerateArray().ShouldContain(f => f.GetProperty("sourceKind").GetString() == "faq");
        trace.GetProperty("confidence").GetDouble().ShouldBeGreaterThanOrEqualTo(0.6);
        trace.GetProperty("prompts").GetString()!.ShouldContain("agent.reply@1");
        trace.GetProperty("models").GetString()!.ShouldContain("fake/fake-writer");
        trace.GetProperty("inputTokens").GetInt64().ShouldBeGreaterThan(0);

        // A price comes from the catalog tool.
        var c2 = "c2" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c2, "قیمت کیف چرم چنده؟");
        var priced = await Eventually(() => Task.FromResult(TextsTo(c2).FirstOrDefault()), "the price answer");
        priced.ShouldContain("۱٬۸۵۰٬۰۰۰ تومان");
        var toolTrace = await TraceAsync(s, (await ConversationAsync(s, c2)).Id);
        toolTrace.GetProperty("outcome").GetString().ShouldBe("sent");
        var call = toolTrace.GetProperty("toolCalls").EnumerateArray().ShouldHaveSingleItem();
        call.GetProperty("name").GetString().ShouldBe("search_products");
        call.GetProperty("result").GetString()!.ShouldContain("1850000");
        toolTrace.GetProperty("guardIssues").GetArrayLength().ShouldBe(0);

        // Two replies, two AI conversations on the plan's meters.
        await Eventually<object>(async () =>
        {
            await using var scope = workers.Host.Services.CreateAsyncScope();
            var entitlements = scope.ServiceProvider.GetRequiredService<IEntitlements>();
            return await entitlements.GetUsageAsync(s.WorkspaceId, BillingMeters.AiMessages, Ct) == 2
                && await entitlements.GetUsageAsync(s.WorkspaceId, BillingMeters.AiConversations, Ct) == 2 ? new object() : null;
        }, "AI usage is metered");

        // Filters.
        var sent = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces?outcome=sent", Ct);
        sent.GetProperty("items").GetArrayLength().ShouldBe(2);
        (await s.Client.GetAsync($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces?outcome=nope", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Client.GetAsync($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hands_off_on_rules_made_up_prices_low_confidence_and_missing_knowledge()
    {
        var (s, _) = await ShopAsync();
        var handoffMessage = AgentProfile.DefaultHandoffMessage;

        // The price guard blocks a made-up price twice, then hands off without a suggestion.
        var c1 = "g1" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c1, "خیلی گرونه، قیمت کیف رو کمتر حساب کنید");
        await Eventually(() => Task.FromResult(TextsTo(c1).Contains(handoffMessage) ? new object() : null), "the handoff message");
        TextsTo(c1).ShouldNotContain(t => t.Contains("۹۹۰", StringComparison.Ordinal));
        var guarded = await ConversationAsync(s, c1);
        guarded.Status.ShouldBe(ConversationStatuses.HandedOff);
        guarded.Reason.ShouldBe("ai:guard_price");
        guarded.Suggested.ShouldBeNull();
        var trace = await TraceAsync(s, guarded.Id);
        trace.GetProperty("outcome").GetString().ShouldBe("handed_off");
        var issues = trace.GetProperty("guardIssues").EnumerateArray().ToList();
        issues.Select(i => i.GetProperty("attempt").GetInt32()).ShouldBe([1, 2]);
        issues.ShouldAllBe(i => i.GetProperty("kind").GetString() == "price");

        // Low confidence hands off with the reply as a suggestion for the person.
        var c2 = "g2" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c2, "یه سوال عجیب دارم");
        var low = await EventuallyValue<(Guid Id, string Status, string? Suggested, string? Reason)>(async () =>
            await ConversationAsync(s, c2) is { Status: ConversationStatuses.HandedOff } c ? c : null, "the low-confidence handoff");
        low.Suggested.ShouldBe("فکر کنم فردا برسه");
        low.Reason.ShouldBe("ai:low_confidence");
        TextsTo(c2).ShouldNotContain("فکر کنم فردا برسه");

        // Asking for a person hands off before any model call.
        var c3 = "g3" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c3, "لطفا با اپراتور صحبت کنم");
        var human = await TraceAsync(s, (await ConversationAsync(s, c3)).Id);
        human.GetProperty("handoffReason").GetString().ShouldBe("human_request");
        human.GetProperty("intent").ValueKind.ShouldBe(JsonValueKind.Null);

        // A complaint goes to a person.
        var c4 = "g4" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c4, "کیفی که فرستادید خراب بود");
        (await TraceAsync(s, (await ConversationAsync(s, c4)).Id)).GetProperty("handoffReason").GetString().ShouldBe("complaint");

        // Missing knowledge becomes an unanswered question; asking again counts it.
        var c5 = "g5" + Guid.NewGuid().ToString("N")[..8];
        var c6 = "g6" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c5, "ساعت کاری فروشگاه حضوری چیه؟");
        await TraceAsync(s, (await ConversationAsync(s, c5)).Id);
        await DmAsync(s, c6, "ساعت کاری فروشگاه حضوری چیه");
        (await TraceAsync(s, (await ConversationAsync(s, c6)).Id)).GetProperty("handoffReason").GetString().ShouldBe("missing_knowledge");
        await Eventually<object>(async () =>
        {
            await using var scope = workers.Host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(s.WorkspaceId);
            var q = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Unanswered.AsNoTracking().SingleOrDefaultAsync(Ct);
            return q is { Count: 2, Status: "open" } ? q : null;
        }, "the unanswered question is counted twice");

        // A handed-off conversation is not answered by the AI again.
        await DmAsync(s, c1, "قیمت کیف چرم چنده؟");
        await Task.Delay(2_000, Ct);
        TextsTo(c1).ShouldNotContain(t => t.Contains("۱٬۸۵۰٬۰۰۰", StringComparison.Ordinal));

        var handedOff = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{s.WorkspaceId}/agent/traces?outcome=handed_off", Ct);
        handedOff.GetProperty("items").GetArrayLength().ShouldBe(6);
    }

    [Fact]
    public async Task The_profile_has_defaults_validation_and_drives_the_pipeline()
    {
        var s = await SeedAsync();
        var url = $"/api/v1/workspaces/{s.WorkspaceId}/agent/profile";
        var initial = await s.Client.GetFromJsonAsync<JsonElement>(url, Ct);
        initial.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        initial.GetProperty("profile").GetProperty("confidenceThreshold").GetDouble().ShouldBe(0.6);
        initial.GetProperty("profile").GetProperty("handoff").GetProperty("vipTags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["vip"]);

        object Profile(string tone = "formal", double threshold = 0.7, string[]? domains = null, string? handoffMessage = null) => new
        {
            name = "دستیار کیف‌شاپ",
            tone,
            brandVoice = "مودب و کوتاه",
            replyInCustomerLanguage = true,
            neverSay = new[] { "ارزان‌ترین بازار" },
            confidenceThreshold = threshold,
            handoff = new { humanRequest = true, complaint = true, discountRequest = true, repeatedLowConfidence = true, keywords = new[] { "مرجوعی فوری" }, vipTags = new[] { "vip", "همکار" } },
            handoffMessage,
            allowedDomains = domains ?? ["https://www.kifshop.ir/"],
        };

        var invalid = await s.Client.PutAsJsonAsync(url, Profile(tone: "rude", threshold: 0.1, domains: ["not a domain"]), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("tone", out _).ShouldBeTrue();
        errors.TryGetProperty("confidenceThreshold", out _).ShouldBeTrue();
        errors.TryGetProperty("allowedDomains", out _).ShouldBeTrue();

        var saved = await (await s.Client.PutAsJsonAsync(url, Profile(), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        saved.GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        saved.GetProperty("profile").GetProperty("allowedDomains").EnumerateArray().Select(d => d.GetString()).ShouldBe(["kifshop.ir"]);
        saved.GetProperty("profile").GetProperty("handoffMessage").ValueKind.ShouldBe(JsonValueKind.Null);

        // A configured keyword hands off, and without a handoff message nothing is sent to the customer.
        var c1 = "p1" + Guid.NewGuid().ToString("N")[..8];
        await DmAsync(s, c1, "یه مرجوعی فوری دارم");
        var trace = await TraceAsync(s, (await ConversationAsync(s, c1)).Id);
        trace.GetProperty("handoffReason").GetString().ShouldBe("keyword");
        trace.GetProperty("threshold").GetDouble().ShouldBe(0.7);
        await Task.Delay(1_000, Ct);
        TextsTo(c1).ShouldBeEmpty();
    }
}
