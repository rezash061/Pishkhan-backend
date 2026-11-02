using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Modules.Payments.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-2.2 Zarinpal payment links, end to end against a fake Zarinpal: the seller's merchant account, a link per order, the customer's
/// payment page, and the callback. Only a payment Zarinpal verifies for the link's own amount pays the order; a forged or repeated
/// callback changes nothing.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PaymentsTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _ = workers;
        _api = new ApiFactory(infra)
        {
            TestServices = s => s.AddHttpClient<ZarinpalGateway>().ConfigurePrimaryHttpMessageHandler(() => new FakeZarinpal()),
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed record Seeded(Guid WorkspaceId, HttpClient Client)
    {
        public string Url => $"/api/v1/workspaces/{WorkspaceId}";
    }

    private HttpClient Customer() => _api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static object Money(long toman) => new { amount = toman * 10, currency = "IRR" };

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    private async Task<Seeded> SeedAsync()
    {
        var client = await _api.SignInAsync(Ct);
        return new Seeded(await ApiFactory.CreateWorkspaceAsync(client, Ct), client);
    }

    /// <summary>A complete order for two of a product with 3 in stock: 2 × 1,200,000 + 50,000 shipping = 2,450,000 Toman.</summary>
    private static async Task<(Guid Order, Guid Product, Guid Variant)> OrderAsync(Seeded s)
    {
        var product = await SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/catalog/products", new
        {
            title = "کیف چرم",
            status = "active",
            price = Money(1_200_000),
            variants = new[] { new { sku = "KC-" + Guid.NewGuid().ToString("N")[..6], attributes = new Dictionary<string, string> { ["رنگ"] = "قهوه‌ای" }, stock = 3, stockPolicy = "track" } },
        }, HttpStatusCode.Created);
        var productId = product.GetProperty("id").GetGuid();
        var variantId = product.GetProperty("variants")[0].GetProperty("id").GetGuid();
        var order = await SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/orders", new
        {
            items = new[] { new { productId, variantId, quantity = 2 } },
            shippingFee = Money(50_000),
            customerName = "سارا",
            phone = "09123456789",
            address = new { text = "اصفهان، خیابان چهارباغ، کوچه ۵، پلاک ۸ کد پستی ۸۱۳۴۸۹۵۶۴۱" },
        }, HttpStatusCode.Created);
        return (order.GetProperty("id").GetGuid(), productId, variantId);
    }

    private static Task ConnectAsync(Seeded s, string merchantId) =>
        SendAsync(s.Client, HttpMethod.Put, $"{s.Url}/payments/merchant-account", new { merchantId }, HttpStatusCode.OK);

    private static Task<JsonElement> LinkAsync(Seeded s, Guid orderId, HttpStatusCode expected = HttpStatusCode.Created) =>
        SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/orders/{orderId}/payment-links", new { }, expected);

    private static async Task<JsonElement> OrderStatusAsync(Seeded s, Guid orderId) => await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/orders/{orderId}", null, HttpStatusCode.OK);

    private static async Task<JsonElement> LinksAsync(Seeded s, Guid orderId) => await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/orders/{orderId}/payment-links", null, HttpStatusCode.OK);

    private static string Token(JsonElement link) => new Uri(link.GetProperty("url").GetString()!).Segments[^1];

    /// <summary>Presses the pay button and returns Zarinpal's authority from the redirect.</summary>
    private async Task<string> StartAsync(string token)
    {
        var response = await Customer().PostAsync($"/pay/{token}", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther, await response.Content.ReadAsStringAsync(Ct));
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).ShouldStartWith("https://payment.zarinpal.com/pg/StartPay/");
        return location.Segments[^1];
    }

    private async Task<HttpResponseMessage> CallbackAsync(string authority, string status) =>
        await Customer().GetAsync($"/pay/callback/zarinpal?Authority={HttpUtility.UrlEncode(authority)}&Status={status}", Ct);

    private async Task<string> PageAsync(string token)
    {
        var response = await Customer().GetAsync($"/pay/{token}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task Eventually(Func<Task<bool>> probe, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!await probe())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(250, Ct);
        }
    }

    [Fact]
    public async Task A_verified_payment_pays_the_order_and_forged_cancelled_or_repeated_callbacks_change_nothing()
    {
        var s = await SeedAsync();
        var (orderId, productId, variantId) = await OrderAsync(s);

        // No merchant account yet: no link, and the order stays a draft.
        var merchant = await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/payments/merchant-account", null, HttpStatusCode.OK);
        merchant.GetProperty("configured").GetBoolean().ShouldBeFalse();
        (await LinkAsync(s, orderId, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("payments.merchant_missing");

        // The merchant ID is validated, shown only by its last four characters and stored encrypted.
        await SendAsync(s.Client, HttpMethod.Put, $"{s.Url}/payments/merchant-account", new { merchantId = "not-a-merchant" }, HttpStatusCode.BadRequest);
        var merchantId = Guid.NewGuid().ToString();
        await ConnectAsync(s, merchantId.ToUpperInvariant());
        merchant = await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/payments/merchant-account", null, HttpStatusCode.OK);
        merchant.GetProperty("merchantIdHint").GetString().ShouldBe(merchantId[^4..]);
        merchant.GetRawText().ShouldNotContain(merchantId[..8]);
        await using (var connection = await workers.Host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            var stored = await Dapper.SqlMapper.ExecuteScalarAsync<string>(connection, "SELECT merchant_id_enc FROM payments.merchant_accounts WHERE workspace_id = @ws", new { ws = s.WorkspaceId });
            stored!.ShouldNotContain(merchantId[..8]);
        }

        var link = await LinkAsync(s, orderId);
        link.GetProperty("status").GetString().ShouldBe("active");
        link.GetProperty("amount").GetProperty("amount").GetInt64().ShouldBe(24_500_000);
        (await OrderStatusAsync(s, orderId)).GetProperty("status").GetString().ShouldBe("awaiting_payment");
        var token = Token(link);

        // The customer's page: the order summary in Toman and a pay button, under a CSP that only lets the form go to Zarinpal.
        var page = await Customer().GetAsync($"/pay/{token}", Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        page.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("form-action 'self' https://payment.zarinpal.com");
        var html = await page.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("۲٬۴۵۰٬۰۰۰ تومان");
        html.ShouldContain("کیف چرم (رنگ: قهوه‌ای)");
        html.ShouldContain("<form method=\"post\">");
        (await Customer().GetAsync("/pay/unknown-token", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Zarinpal is asked for the link's amount, in Rial, with our callback.
        var forged = await StartAsync(token);
        var opened = FakeZarinpal.Get(forged);
        opened.Amount.ShouldBe(24_500_000);
        opened.MerchantId.ShouldBe(merchantId);
        opened.Callback.ShouldEndWith("/pay/callback/zarinpal");

        // A callback saying OK for a payment the customer never made: Zarinpal does not verify it, nothing is paid.
        var response = await CallbackAsync(forged, "OK");
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().ShouldEndWith("/pay/" + token);
        FakeZarinpal.Get(forged).VerifiedAmount.ShouldBe(24_500_000);
        var links = await LinksAsync(s, orderId);
        links[0].GetProperty("status").GetString().ShouldBe("active");
        links[0].GetProperty("lastError").GetString().ShouldBe("-51");
        (await OrderStatusAsync(s, orderId)).GetProperty("status").GetString().ShouldBe("awaiting_payment");
        (await PageAsync(token)).ShouldContain("دوباره تلاش کنید");

        // The customer leaves the gateway: the attempt fails without asking Zarinpal.
        var left = await StartAsync(token);
        (await CallbackAsync(left, "NOK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        FakeZarinpal.Get(left).Verifications.ShouldBe(0);
        (await LinksAsync(s, orderId))[0].GetProperty("lastError").GetString().ShouldBe("cancelled");

        // Unknown authorities are not ours.
        (await CallbackAsync("A" + new string('0', 35), "OK")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The real payment: verified, the link is paid, the order moves to paid and its stock is taken.
        var paid = await StartAsync(token);
        FakeZarinpal.Pay(paid);
        (await CallbackAsync(paid, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        links = await LinksAsync(s, orderId);
        links[0].GetProperty("status").GetString().ShouldBe("paid");
        links[0].GetProperty("referenceId").GetString().ShouldBe("201");
        links[0].GetProperty("cardMask").GetString().ShouldBe("502229******5995");
        links[0].GetProperty("attempts").GetInt32().ShouldBe(3);
        var paidPage = await PageAsync(token);
        paidPage.ShouldContain("پرداخت با موفقیت انجام شد");
        paidPage.ShouldContain("۲۰۱");
        paidPage.ShouldNotContain("<form");
        await Eventually(async () => (await OrderStatusAsync(s, orderId)).GetProperty("status").GetString() == "paid", "order paid");
        await Eventually(
            async () => (await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/catalog/products/{productId}", null, HttpStatusCode.OK))
                .GetProperty("variants").EnumerateArray().First(v => v.GetProperty("id").GetGuid() == variantId).GetProperty("stock").GetInt32() == 1,
            "stock taken");

        // The same callback again, and a late one for the forged attempt: nothing is verified or paid twice.
        (await CallbackAsync(paid, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        (await CallbackAsync(forged, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        FakeZarinpal.Get(paid).Verifications.ShouldBe(1);
        FakeZarinpal.Get(forged).Verifications.ShouldBe(1);
        var timeline = await SendAsync(s.Client, HttpMethod.Get, $"{s.Url}/orders/{orderId}/timeline", null, HttpStatusCode.OK);
        timeline.GetRawText().ShouldContain("کد پیگیری 201");
        (await Customer().PostAsync($"/pay/{token}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // paid: the page again, no new payment
        (await LinkAsync(s, orderId, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("orders.not_payable");
    }

    [Fact]
    public async Task A_new_link_an_order_back_to_draft_or_a_cancel_stops_the_older_links()
    {
        var s = await SeedAsync();
        var (orderId, _, _) = await OrderAsync(s);
        await ConnectAsync(s, Guid.NewGuid().ToString());

        // A second link cancels the first.
        var first = await LinkAsync(s, orderId);
        var second = await LinkAsync(s, orderId);
        var links = await LinksAsync(s, orderId);
        links.GetArrayLength().ShouldBe(2);
        links.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == first.GetProperty("id").GetGuid()).GetProperty("status").GetString().ShouldBe("cancelled");
        (await PageAsync(Token(first))).ShouldContain("این لینک پرداخت دیگر فعال نیست");
        (await Customer().PostAsync($"/pay/{Token(first)}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The seller cancels the second; a second cancel conflicts.
        var cancelled = await SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/payment-links/{second.GetProperty("id").GetGuid()}/cancel", null, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        (await SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/payment-links/{second.GetProperty("id").GetGuid()}/cancel", null, HttpStatusCode.Conflict))
            .GetProperty("code").GetString().ShouldBe("payments.link_not_active");

        // The order goes back to draft to be edited: its active link is cancelled.
        var third = await LinkAsync(s, orderId);
        await SendAsync(s.Client, HttpMethod.Post, $"{s.Url}/orders/{orderId}/status", new { status = "draft" }, HttpStatusCode.OK);
        await Eventually(
            async () => (await LinksAsync(s, orderId)).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == third.GetProperty("id").GetGuid())
                .GetProperty("status").GetString() == "cancelled",
            "link cancelled with the order back to draft");

        // Disconnecting the merchant account stops new links.
        await SendAsync(s.Client, HttpMethod.Delete, $"{s.Url}/payments/merchant-account", null, HttpStatusCode.NoContent);
        await LinkAsync(s, orderId, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Zarinpal_down_leaves_the_link_payable()
    {
        var s = await SeedAsync();
        var (orderId, _, _) = await OrderAsync(s);
        await ConnectAsync(s, Guid.NewGuid().ToString());
        var token = Token(await LinkAsync(s, orderId));

        FakeZarinpal.Down = true;
        try
        {
            var response = await Customer().PostAsync($"/pay/{token}", null, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("اتصال به درگاه پرداخت برقرار نشد");
        }
        finally
        {
            FakeZarinpal.Down = false;
        }

        (await StartAsync(token)).ShouldNotBeNullOrEmpty();
    }
}
