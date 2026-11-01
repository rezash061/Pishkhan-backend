using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-2.1 Orders, end to end: panel orders priced from the catalog through the whole lifecycle (with stock taken on payment and put
/// back on refund), the invoice and timeline, and the AI's <c>create_order</c> tool saving one draft per conversation with catalog prices.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed partial class OrdersTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
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

    private sealed record Seeded(Guid WorkspaceId, string AccountExternalId, HttpClient Client)
    {
        public string Orders => $"/api/v1/workspaces/{WorkspaceId}/orders";

        public string Catalog => $"/api/v1/workspaces/{WorkspaceId}/catalog";
    }

    private async Task<Seeded> SeedAsync(bool ai = false)
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1785" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        db.Accounts.Add(ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        if (ai)
        {
            (await client.PutAsJsonAsync($"/api/v1/workspaces/{ws}/automation/settings", new { aiEnabled = true }, Ct)).EnsureSuccessStatusCode();
        }

        return new Seeded(ws, externalId, client);
    }

    private static object Money(long toman) => new { amount = toman * 10, currency = "IRR" };

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    private static Task<JsonElement> PostAsync(Seeded s, string path, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        SendAsync(s.Client, HttpMethod.Post, path, body, expected);

    private static Task<JsonElement> GetAsync(Seeded s, string path) => SendAsync(s.Client, HttpMethod.Get, path, null, HttpStatusCode.OK);

    private static Task<JsonElement> StatusAsync(Seeded s, Guid orderId, string status, HttpStatusCode expected = HttpStatusCode.OK, string? note = null) =>
        PostAsync(s, $"{s.Orders}/{orderId}/status", new { status, note }, expected);

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

    /// <summary>A product with a counted variant (3 in stock) and an unlimited one, at 2,500,000 and 2,700,000 Toman.</summary>
    private static async Task<(Guid Product, Guid Counted, Guid Unlimited)> ProductAsync(Seeded s)
    {
        var product = await PostAsync(s, $"{s.Catalog}/products", new
        {
            title = "مانتو کتان",
            status = "active",
            price = Money(2_500_000),
            variants = new object[]
            {
                new { sku = "MK-M-" + Guid.NewGuid().ToString("N")[..6], attributes = new Dictionary<string, string> { ["سایز"] = "M" }, stock = 3, stockPolicy = "track" },
                new { sku = "MK-L-" + Guid.NewGuid().ToString("N")[..6], attributes = new Dictionary<string, string> { ["سایز"] = "L" }, price = Money(2_700_000), stockPolicy = "unlimited" },
            },
        });
        var variants = product.GetProperty("variants").EnumerateArray().ToList();
        return (product.GetProperty("id").GetGuid(), variants[0].GetProperty("id").GetGuid(), variants[1].GetProperty("id").GetGuid());
    }

    private static async Task<int?> StockAsync(Seeded s, Guid productId, Guid variantId)
    {
        var product = await GetAsync(s, $"{s.Catalog}/products/{productId}");
        var stock = product.GetProperty("variants").EnumerateArray().First(v => v.GetProperty("id").GetGuid() == variantId).GetProperty("stock");
        return stock.ValueKind == JsonValueKind.Null ? null : stock.GetInt32();
    }

    private static Task<string> StockReachedAsync(Seeded s, Guid productId, Guid variantId, int expected) =>
        Eventually(async () => await StockAsync(s, productId, variantId) == expected ? "ok" : null, $"stock {expected}");

    [Fact]
    public async Task A_panel_order_is_priced_from_the_catalog_and_runs_through_its_lifecycle_taking_and_returning_stock()
    {
        var s = await SeedAsync();
        var (product, counted, unlimited) = await ProductAsync(s);

        // Catalog lines take the catalog price, whatever price the request carries; a custom line keeps its own.
        var order = await PostAsync(s, s.Orders, new
        {
            items = new object[]
            {
                new { productId = product, variantId = counted, quantity = 2, unitPrice = Money(1) },
                new { productId = product, variantId = unlimited, quantity = 1 },
                new { title = "بسته‌بندی کادو", quantity = 1, unitPrice = Money(50_000) },
            },
            shippingFee = Money(60_000),
            discount = Money(10_000),
            customerName = "مریم",
            phone = "۰۹۱۲۳۴۵۶۷۸۹",
            address = new { text = "شیراز، خیابان زند، کوچه ۱۲، پلاک ۴ کد پستی ۷۱۳۴۸۹۵۶۴۱" },
        });
        var id = order.GetProperty("id").GetGuid();
        order.GetProperty("status").GetString().ShouldBe("draft");
        order.GetProperty("source").GetString().ShouldBe("panel");
        var lines = order.GetProperty("items").EnumerateArray().ToList();
        lines[0].GetProperty("unitPrice").GetProperty("amount").GetInt64().ShouldBe(25_000_000);
        lines[0].GetProperty("variantLabel").GetString().ShouldBe("سایز: M");
        lines[1].GetProperty("unitPrice").GetProperty("amount").GetInt64().ShouldBe(27_000_000);
        lines[2].GetProperty("productId").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("subtotal").GetProperty("amount").GetInt64().ShouldBe(77_500_000);
        order.GetProperty("total").GetProperty("amount").GetInt64().ShouldBe(78_000_000);
        order.GetProperty("phone").GetString().ShouldBe("+989123456789");
        var address = order.GetProperty("address");
        address.GetProperty("provinceCode").GetString().ShouldBe("fars");
        address.GetProperty("city").GetString().ShouldBe("شیراز");
        address.GetProperty("postalCode").GetString().ShouldBe("7134895641");
        order.GetProperty("missing").GetArrayLength().ShouldBe(0);
        order.GetProperty("nextStatuses").EnumerateArray().Select(e => e.GetString()).ShouldBe(["awaiting_payment", "cancelled"], ignoreOrder: true);

        // The phone is stored encrypted, never as text.
        await using (var connection = await workers.Host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            var stored = await Dapper.SqlMapper.ExecuteScalarAsync<string>(connection, "SELECT phone_enc FROM orders.orders WHERE id = @id", new { id });
            stored!.ShouldNotContain("9123456789");
        }

        // More than the stock, unknown products and bad postal codes are refused.
        await PostAsync(s, s.Orders, new { items = new[] { new { productId = product, variantId = counted, quantity = 4 } } }, HttpStatusCode.BadRequest);
        var unknown = await PostAsync(s, s.Orders, new { items = new[] { new { productId = Guid.NewGuid(), quantity = 1 } } }, HttpStatusCode.BadRequest);
        unknown.GetProperty("errors").GetProperty("items[0]")[0].GetString().ShouldBe("orders.product_not_found");
        await PostAsync(s, s.Orders, new { items = new[] { new { productId = product, quantity = 1 } } }, HttpStatusCode.BadRequest); // two variants: which one?
        await PostAsync(s, s.Orders, new { address = new { line = "خیابان زند", postalCode = "1234" } }, HttpStatusCode.BadRequest);

        // A draft is edited; the next order gets the next number.
        var edited = await SendAsync(s.Client, HttpMethod.Put, $"{s.Orders}/{id}", new
        {
            items = new[] { new { productId = product, variantId = counted, quantity = 2 } },
            shippingFee = Money(60_000),
            customerName = "مریم",
            phone = "09123456789",
            address = new { line = "خیابان زند، کوچه ۱۲", city = "شیراز", postalCode = "7134895641" },
            note = "لطفا زنگ بزنید",
        }, HttpStatusCode.OK);
        edited.GetProperty("total").GetProperty("amount").GetInt64().ShouldBe(50_600_000);
        edited.GetProperty("address").GetProperty("province").GetString().ShouldBe("فارس");
        var second = await PostAsync(s, s.Orders, new { items = new[] { new { productId = product, variantId = unlimited, quantity = 1 } } });
        second.GetProperty("number").GetInt64().ShouldBe(order.GetProperty("number").GetInt64() + 1);

        // The lifecycle: no skipping, no editing after the draft, stock taken on payment.
        await StatusAsync(s, id, "paid", HttpStatusCode.Conflict);
        await StatusAsync(s, id, "awaiting_payment");
        await SendAsync(s.Client, HttpMethod.Put, $"{s.Orders}/{id}", new { items = Array.Empty<object>() }, HttpStatusCode.Conflict);
        var paid = await StatusAsync(s, id, "paid", note: "کارت به کارت");
        paid.GetProperty("paidAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        await StockReachedAsync(s, product, counted, 1);
        await StatusAsync(s, id, "preparing");
        await StatusAsync(s, id, "cancelled", HttpStatusCode.Conflict);
        await StatusAsync(s, id, "refunded", note: "مشتری منصرف شد");
        await StockReachedAsync(s, product, counted, 3);
        (await StockAsync(s, product, unlimited)).ShouldBeNull();

        // Timeline, invoice and filters.
        var timeline = (await GetAsync(s, $"{s.Orders}/{id}/timeline")).EnumerateArray().ToList();
        timeline.Select(e => e.GetProperty("kind").GetString()).ShouldBe(["created", "edited", "status_changed", "status_changed", "status_changed", "status_changed"]);
        timeline[^1].GetProperty("toStatus").GetString().ShouldBe("refunded");
        timeline[^1].GetProperty("note").GetString().ShouldBe("مشتری منصرف شد");
        timeline[^1].GetProperty("actorType").GetString().ShouldBe("user");
        await PostAsync(s, $"{s.Orders}/{id}/notes", new { note = "پول برگشت داده شد" });

        var invoice = await GetAsync(s, $"{s.Orders}/{id}/invoice");
        invoice.GetProperty("total").GetString().ShouldBe("۵٬۰۶۰٬۰۰۰ تومان");
        invoice.GetProperty("statusText").GetString().ShouldBe("مسترد‌شده");
        invoice.GetProperty("phone").GetString().ShouldBe("۰۹۱۲۳۴۵۶۷۸۹");
        invoice.GetProperty("date").GetString()!.ShouldStartWith("۱۴۰۵/");
        invoice.GetProperty("paidDate").GetString().ShouldNotBeNull();

        var refunded = await GetAsync(s, $"{s.Orders}?status=refunded&status=completed");
        refunded.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("id").GetGuid()).ShouldBe([id]);
        var byPhone = await GetAsync(s, $"{s.Orders}?phone=0912%20345%206789");
        byPhone.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await GetAsync(s, $"{s.Orders}?number={second.GetProperty("number").GetInt64()}")).GetProperty("items")[0].GetProperty("id").GetGuid()
            .ShouldBe(second.GetProperty("id").GetGuid());

        // Reference data for the address form.
        (await GetAsync(s, $"{s.Orders}/provinces")).GetArrayLength().ShouldBe(31);
        var parsed = await PostAsync(s, $"{s.Orders}/parse-address", new { text = "کرج، گوهردشت، بلوار موذن" }, HttpStatusCode.OK);
        parsed.GetProperty("provinceCode").GetString().ShouldBe("alborz");
    }

    // The AI orders test. The scripted writer: on «سفارش <product id> <quantity>» it calls create_order once, then answers with the
    // number and total the tool returned.
    private static ChatResponse? Respond(string model, IList<ChatMessage> messages, ChatOptions? options)
    {
        var user = messages.Last(m => m.Role == ChatRole.User).Text;
        if (model == "fake-classifier")
        {
            return Text(JsonSerializer.Serialize(new { intent = "order", language = "fa", sentiment = "positive", confidence = 0.9 }));
        }

        if (model != "fake-writer" || OrderRequest().Match(user) is not { Success: true } m)
        {
            return null;
        }

        var toolResult = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (toolResult is null)
        {
            var item = JsonSerializer.SerializeToElement(new { productId = m.Groups["id"].Value, variantId = m.Groups["variant"].Value, quantity = int.Parse(m.Groups["qty"].Value, System.Globalization.CultureInfo.InvariantCulture) });
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "create_order", new Dictionary<string, object?>
            {
                ["items"] = JsonSerializer.SerializeToElement(new[] { item }),
                ["customerName"] = "سارا",
                ["phone"] = "09121112233",
                ["address"] = "تهران، خیابان ولیعصر، کوچه یاس، پلاک ۸",
                ["note"] = null,
            })]));
        }

        var result = JsonDocument.Parse(toolResult.Result!.ToString()!).RootElement;
        if (!result.TryGetProperty("ok", out _))
        {
            var error = result.GetProperty("error").GetString() == "out_of_stock" ? "متاسفانه موجودی کافی نیست" : "خطا در ثبت سفارش";
            return Text(JsonSerializer.Serialize(new { reply = error, confidence = 0.9, answered = true }));
        }

        var reply = $"سفارش شماره {result.GetProperty("orderNumber").GetString()} ثبت شد. جمع: {result.GetProperty("total").GetString()}. کد پستی رو هم بفرمایید.";
        return Text(JsonSerializer.Serialize(new { reply, confidence = 0.9, answered = true }));
    }

    [GeneratedRegex(@"سفارش (?<id>[0-9a-f-]{36}) (?<variant>[0-9a-f-]{36}) (?<qty>\d+)")]
    private static partial Regex OrderRequest();

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

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

    private static List<string> TextsTo(string customer) =>
        [.. FakeInstagram.Sent.Select(b => JsonDocument.Parse(b).RootElement)
            .Where(e => e.TryGetProperty("recipient", out var r) && r.TryGetProperty("id", out var i) && i.GetString() == customer)
            .Select(e => e.GetProperty("message"))
            .Where(msg => msg.TryGetProperty("text", out _))
            .Select(msg => msg.GetProperty("text").GetString()!)];

    [Fact]
    public async Task The_ai_saves_one_draft_order_per_conversation_with_catalog_prices()
    {
        var s = await SeedAsync(ai: true);
        var (product, counted, _) = await ProductAsync(s);
        var customer = "cust-" + Guid.NewGuid().ToString("N")[..10];

        await DmAsync(s, customer, $"سفارش {product} {counted} 2");
        var first = await Eventually(async () => TextsTo(customer).FirstOrDefault(t => t.StartsWith("سفارش شماره", StringComparison.Ordinal)), "first AI reply");
        first.ShouldContain("۵٬۰۰۰٬۰۰۰ تومان");

        var page = await GetAsync(s, s.Orders);
        var order = page.GetProperty("items").EnumerateArray().Single();
        order.GetProperty("source").GetString().ShouldBe("ai");
        order.GetProperty("status").GetString().ShouldBe("draft");
        order.GetProperty("conversationId").ValueKind.ShouldBe(JsonValueKind.String);
        order.GetProperty("customerName").GetString().ShouldBe("سارا");
        order.GetProperty("phone").GetString().ShouldBe("+989121112233");
        order.GetProperty("address").GetProperty("city").GetString().ShouldBe("تهران");
        order.GetProperty("items")[0].GetProperty("unitPrice").GetProperty("amount").GetInt64().ShouldBe(25_000_000);
        order.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ShouldBe(["postalCode"]);
        first.ShouldContain(PersianDigitsOf(order.GetProperty("number").GetInt64()));

        // The customer changes the quantity: the same draft is updated, not a second order.
        await DmAsync(s, customer, $"سفارش {product} {counted} 3");
        await Eventually(async () => TextsTo(customer).Any(t => t.Contains("۷٬۵۰۰٬۰۰۰ تومان", StringComparison.Ordinal)) ? "ok" : null, "second AI reply");
        page = await GetAsync(s, s.Orders);
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        page.GetProperty("items")[0].GetProperty("items")[0].GetProperty("quantity").GetInt32().ShouldBe(3);
        var timeline = await GetAsync(s, $"{s.Orders}/{page.GetProperty("items")[0].GetProperty("id").GetGuid()}/timeline");
        timeline.EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.GetProperty("actorType").GetString())).ShouldBe([("created", "ai"), ("edited", "ai")]);

        // More than the stock is not ordered; the tool's error reaches the model instead.
        await DmAsync(s, customer, $"سفارش {product} {counted} 9");
        await Eventually(async () => TextsTo(customer).Any(t => t == "متاسفانه موجودی کافی نیست") ? "ok" : null, "out of stock reply");
        (await GetAsync(s, s.Orders)).GetProperty("items")[0].GetProperty("items")[0].GetProperty("quantity").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task The_test_chat_prices_an_order_but_saves_nothing()
    {
        var s = await SeedAsync();
        var (product, counted, _) = await ProductAsync(s);
        _api.Services.GetRequiredService<FakeChatScript>().Respond = Respond; // test mode runs in the Api

        var result = await PostAsync(s, $"/api/v1/workspaces/{s.WorkspaceId}/agent/sandbox", new { text = $"سفارش {product} {counted} 1" }, HttpStatusCode.OK);
        result.GetRawText().ShouldContain("۲٬۵۰۰٬۰۰۰ تومان");
        (await GetAsync(s, s.Orders)).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    private static string PersianDigitsOf(long n) => Pishkhan.Persian.PersianDigits.ToPersian(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
