using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Modules.Payments.Services;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-2.8 subscription checkout, end to end against a fake Zarinpal: a seller buys a plan, renews it, upgrades with the unused part
/// credited and downgrades from the end of the paid time. Only a payment Zarinpal verifies for the checkout's own amount starts a
/// subscription; a forged, cancelled or repeated callback changes nothing.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class CheckoutTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private static readonly string MerchantId = Guid.NewGuid().ToString();

    private ApiFactory _api = null!;
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _ = workers;
        _api = new ApiFactory(infra, new Dictionary<string, string?>
        {
            ["Billing:Checkout:MerchantId"] = MerchantId,
            ["Billing:Checkout:ApiBaseUrl"] = "https://api.pishkhan.test",
            ["Billing:Checkout:ReturnUrl"] = "https://panel.pishkhan.test/billing",
        })
        {
            TestServices = s => s.AddHttpClient<ZarinpalGateway>().ConfigurePrimaryHttpMessageHandler(() => new FakeZarinpal()),
        };
        _admin = await AdminHost.StartAsync(infra, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    private sealed record Seller(Guid WorkspaceId, HttpClient Client)
    {
        public string Url => $"/api/v1/workspaces/{WorkspaceId}/billing";
    }

    private async Task<Seller> SellerAsync()
    {
        var client = await _api.SignInAsync(Ct);
        return new Seller(await ApiFactory.CreateWorkspaceAsync(client, Ct), client);
    }

    private async Task<Guid> PlanAsync(string code, long monthlyRial, long? annualRial, bool isPublic = true, bool publish = true)
    {
        var finance = _admin.StaffClient(StaffRoles.Finance, stepUp: true);
        var created = await finance.PostAsJsonAsync("/admin/v1/plans", new
        {
            code,
            plan = new
            {
                name = "بسته " + code,
                monthly = new { amount = monthlyRial, currency = "IRR" },
                annual = annualRial is null ? null : new { amount = annualRial.Value, currency = "IRR" },
                limits = new Dictionary<string, int?>(),
                isPublic,
                isFree = false,
                highlighted = false,
                sortOrder = 50,
            },
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        if (publish)
        {
            (await finance.PostAsJsonAsync($"/admin/v1/plans/{id}/publish", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return id;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, string url, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{url}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    private static long Rial(JsonElement e, string name) => e.GetProperty(name).GetProperty("amount").GetInt64();

    /// <summary>Opens the checkout and returns (checkout id, Zarinpal authority).</summary>
    private static async Task<(Guid Id, string Authority)> StartAsync(Seller s, Guid planId, string cycle = "monthly")
    {
        var started = await SendAsync(s.Client, s.Url + "/checkout", new { planId, cycle }, HttpStatusCode.Created);
        var url = started.GetProperty("paymentUrl").GetString()!;
        url.ShouldStartWith("https://payment.zarinpal.com/pg/StartPay/");
        return (started.GetProperty("checkout").GetProperty("id").GetGuid(), Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]));
    }

    private HttpClient Browser() => _api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpResponseMessage> CallbackAsync(string authority, string status) =>
        await Browser().GetAsync($"/billing/checkout/callback?Authority={HttpUtility.UrlEncode(authority)}&Status={status}", Ct);

    private async Task<JsonElement> PayAsync(Seller s, Guid planId, string cycle = "monthly")
    {
        var (id, authority) = await StartAsync(s, planId, cycle);
        FakeZarinpal.Pay(authority);
        var back = await CallbackAsync(authority, "OK");
        back.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        back.Headers.Location!.ToString().ShouldBe($"https://panel.pishkhan.test/billing?checkout={id}&status=paid");
        return await s.Client.GetFromJsonAsync<JsonElement>($"{s.Url}/checkouts/{id}", Ct);
    }

    private static async Task<JsonElement> CurrentAsync(Seller s) => await s.Client.GetFromJsonAsync<JsonElement>(s.Url + "/subscription", Ct);

    [Fact]
    public async Task A_seller_buys_renews_upgrades_and_downgrades_and_only_verified_payments_count()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        var basic = await PlanAsync("basic-" + marker, 5_000_000, 50_000_000);
        var pro = await PlanAsync("pro-" + marker, 10_000_000, null);
        var hidden = await PlanAsync("hidden-" + marker, 1_000_000, null, isPublic: false);
        var draft = await PlanAsync("draft-" + marker, 1_000_000, null, publish: false);

        var s = await SellerAsync();

        // The price list carries each plan's id; the panel's list also says what buying each would be.
        // (The public list is cached and invalidated through the bus, so wait for the new plan.)
        JsonElement? listed = null;
        for (var i = 0; i < 100 && listed is null; i++)
        {
            var priceList = await _api.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/public/plans", Ct);
            listed = priceList.EnumerateArray().Where(p => p.GetProperty("code").GetString() == "basic-" + marker).Select(p => (JsonElement?)p).FirstOrDefault();
            if (listed is null)
            {
                await Task.Delay(200, Ct);
            }
        }

        listed.ShouldNotBeNull().GetProperty("id").GetGuid().ShouldBe(basic);
        var options = await s.Client.GetFromJsonAsync<JsonElement>(s.Url + "/plans", Ct);
        var basicOption = options.EnumerateArray().Single(o => o.GetProperty("plan").GetProperty("id").GetGuid() == basic);
        basicOption.GetProperty("kind").GetString().ShouldBe("new");
        basicOption.GetProperty("current").GetBoolean().ShouldBeFalse();
        options.EnumerateArray().ShouldNotContain(o => o.GetProperty("plan").GetProperty("id").GetGuid() == hidden);

        // Quotes: a workspace without a paid plan buys from now; plans that are not on sale and missing cycles are refused.
        var quote = await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = basic, cycle = "annual" }, HttpStatusCode.OK);
        quote.GetProperty("kind").GetString().ShouldBe("new");
        Rial(quote, "amount").ShouldBe(50_000_000);
        quote.GetProperty("endsAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(364.9), DateTimeOffset.UtcNow.AddDays(365.1));
        Code(await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = pro, cycle = "annual" }, HttpStatusCode.UnprocessableEntity))
            .ShouldBe("billing.checkout.cycle_not_offered");
        Code(await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = hidden }, HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.checkout.plan_not_for_sale");
        Code(await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = draft }, HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.checkout.plan_not_for_sale");
        (await s.Client.PostAsJsonAsync(s.Url + "/checkout/quote", new { planId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Client.PostAsJsonAsync(s.Url + "/checkout/quote", new { planId = basic, cycle = "weekly" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Zarinpal is asked for the checkout's amount, into Pishkhan's account, with the billing callback.
        var (cancelledId, cancelled) = await StartAsync(s, basic);
        FakeZarinpal.Get(cancelled).Amount.ShouldBe(5_000_000);
        FakeZarinpal.Get(cancelled).MerchantId.ShouldBe(MerchantId);
        FakeZarinpal.Get(cancelled).Callback.ShouldBe("https://api.pishkhan.test/billing/checkout/callback");

        // The seller leaves the gateway: failed without asking Zarinpal.
        var back = await CallbackAsync(cancelled, "NOK");
        back.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        back.Headers.Location!.ToString().ShouldBe($"https://panel.pishkhan.test/billing?checkout={cancelledId}&status=failed");
        FakeZarinpal.Get(cancelled).Verifications.ShouldBe(0);
        var failed = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Url}/checkouts/{cancelledId}", Ct);
        failed.GetProperty("status").GetString().ShouldBe("failed");
        failed.GetProperty("error").GetString().ShouldBe("cancelled");

        // OK for a payment never made: Zarinpal does not verify it, and nothing starts.
        var (_, forged) = await StartAsync(s, basic);
        (await CallbackAsync(forged, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        FakeZarinpal.Get(forged).VerifiedAmount.ShouldBe(5_000_000);
        (await CurrentAsync(s)).GetProperty("source").GetString().ShouldNotBe("subscription");
        (await CallbackAsync("A" + new string('0', 35), "OK")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The real payment: an invoice, and the plan from now for 30 days.
        var paid = await PayAsync(s, basic);
        paid.GetProperty("status").GetString().ShouldBe("paid");
        paid.GetProperty("kind").GetString().ShouldBe("new");
        paid.GetProperty("referenceId").GetString().ShouldBe("201");
        paid.GetProperty("cardMask").GetString().ShouldBe("502229******5995");
        var firstInvoice = paid.GetProperty("invoiceNumber").GetInt64();
        firstInvoice.ShouldBeGreaterThanOrEqualTo(1001);
        var current = await CurrentAsync(s);
        current.GetProperty("source").GetString().ShouldBe("subscription");
        current.GetProperty("planId").GetGuid().ShouldBe(basic);
        var sub = current.GetProperty("subscription");
        sub.GetProperty("state").GetString().ShouldBe("active");
        sub.GetProperty("cycle").GetString().ShouldBe("monthly");
        sub.GetProperty("checkoutId").GetGuid().ShouldBe(paid.GetProperty("id").GetGuid());
        var firstEnd = sub.GetProperty("endsAt").GetDateTimeOffset();
        firstEnd.ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));

        // Repeated and late callbacks: nothing verified or paid twice.
        (await CallbackAsync(forged, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        (await CallbackAsync(cancelled, "OK")).StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        FakeZarinpal.Get(forged).Verifications.ShouldBe(1);
        FakeZarinpal.Get(cancelled).Verifications.ShouldBe(0);

        // Renewal: the same plan starts when the paid time ends; the current one is unchanged.
        quote = await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = basic }, HttpStatusCode.OK);
        quote.GetProperty("kind").GetString().ShouldBe("renewal");
        quote.GetProperty("startsAt").GetDateTimeOffset().ShouldBe(firstEnd, TimeSpan.FromSeconds(1));
        var renewal = await PayAsync(s, basic);
        renewal.GetProperty("kind").GetString().ShouldBe("renewal");
        renewal.GetProperty("invoiceNumber").GetInt64().ShouldBeGreaterThan(firstInvoice);
        current = await CurrentAsync(s);
        current.GetProperty("subscription").GetProperty("endsAt").GetDateTimeOffset().ShouldBe(firstEnd, TimeSpan.FromSeconds(1));
        var upcoming = current.GetProperty("upcoming");
        upcoming.GetArrayLength().ShouldBe(1);
        upcoming[0].GetProperty("state").GetString().ShouldBe("scheduled");
        upcoming[0].GetProperty("startsAt").GetDateTimeOffset().ShouldBe(firstEnd, TimeSpan.FromSeconds(1));

        // An upgrade while a later period is already paid for is refused.
        Code(await SendAsync(s.Client, s.Url + "/checkout/quote", new { planId = pro }, HttpStatusCode.Conflict)).ShouldBe("billing.checkout.scheduled_pending");

        var list = await s.Client.GetFromJsonAsync<JsonElement>(s.Url + "/checkouts", Ct);
        list.EnumerateArray().Count(c => c.GetProperty("status").GetString() == "paid").ShouldBe(2);
        list.GetArrayLength().ShouldBe(4);

        // Another seller: upgrade now with the unused part credited, then a downgrade from the end of the upgraded period.
        var t = await SellerAsync();
        await PayAsync(t, basic);
        quote = await SendAsync(t.Client, t.Url + "/checkout/quote", new { planId = pro }, HttpStatusCode.OK);
        quote.GetProperty("kind").GetString().ShouldBe("upgrade");
        Rial(quote, "credit").ShouldBeInRange(4_990_000, 5_000_000);
        Rial(quote, "amount").ShouldBe(10_000_000 - Rial(quote, "credit"));
        var upgrade = await PayAsync(t, pro);
        upgrade.GetProperty("kind").GetString().ShouldBe("upgrade");
        current = await CurrentAsync(t);
        current.GetProperty("planId").GetGuid().ShouldBe(pro);
        options = await t.Client.GetFromJsonAsync<JsonElement>(t.Url + "/plans", Ct);
        var proOption = options.EnumerateArray().Single(o => o.GetProperty("plan").GetProperty("id").GetGuid() == pro);
        proOption.GetProperty("current").GetBoolean().ShouldBeTrue();
        proOption.GetProperty("kind").GetString().ShouldBe("renewal");
        options.EnumerateArray().Single(o => o.GetProperty("plan").GetProperty("id").GetGuid() == basic).GetProperty("kind").GetString().ShouldBe("downgrade");
        var proEnd = current.GetProperty("subscription").GetProperty("endsAt").GetDateTimeOffset();
        proEnd.ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));

        quote = await SendAsync(t.Client, t.Url + "/checkout/quote", new { planId = basic }, HttpStatusCode.OK);
        quote.GetProperty("kind").GetString().ShouldBe("downgrade");
        quote.GetProperty("startsAt").GetDateTimeOffset().ShouldBe(proEnd, TimeSpan.FromSeconds(1));
        await PayAsync(t, basic);
        current = await CurrentAsync(t);
        current.GetProperty("planId").GetGuid().ShouldBe(pro);
        current.GetProperty("upcoming")[0].GetProperty("planId").GetGuid().ShouldBe(basic);

        // Another workspace's checkout is not found.
        (await t.Client.GetAsync($"{t.Url}/checkouts/{cancelledId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_see_payments_and_subscriptions_across_workspaces_and_Finance_records_refunds()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        var plan = await PlanAsync("refund-" + marker, 3_000_000, null);
        var s = await SellerAsync();
        var paid = await PayAsync(s, plan);
        var checkoutId = paid.GetProperty("id").GetGuid();

        // Every staff reader sees the payment with its workspace; the default list is paid payments.
        var reader = _admin.StaffClient(StaffRoles.ReadOnly);
        var payments = await reader.GetFromJsonAsync<JsonElement>($"/admin/v1/billing/payments?workspaceId={s.WorkspaceId}", Ct);
        var row = payments.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("id").GetGuid().ShouldBe(checkoutId);
        row.GetProperty("status").GetString().ShouldBe("paid");
        row.GetProperty("amount").GetInt64().ShouldBe(3_000_000);
        row.GetProperty("refunded").GetInt64().ShouldBe(0);
        row.GetProperty("workspaceName").GetString().ShouldNotBeNullOrEmpty();
        (await reader.GetAsync("/admin/v1/billing/payments?status=weird", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var subscriptions = await reader.GetFromJsonAsync<JsonElement>($"/admin/v1/billing/subscriptions?workspaceId={s.WorkspaceId}", Ct);
        var sub = subscriptions.GetProperty("items").EnumerateArray().Single();
        sub.GetProperty("subscription").GetProperty("planId").GetGuid().ShouldBe(plan);
        sub.GetProperty("subscription").GetProperty("checkoutId").GetGuid().ShouldBe(checkoutId);

        // Refunds need Finance and step-up, a reason, and at most what is left.
        var url = $"/admin/v1/billing/payments/{checkoutId}/refunds";
        (await reader.PostAsJsonAsync(url, new { amount = 1_000_000, reason = "مشتری انصراف داد" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.Finance).PostAsJsonAsync(url, new { amount = 1_000_000, reason = "مشتری انصراف داد" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var finance = _admin.StaffClient(StaffRoles.Finance, stepUp: true);
        await SendAsync(finance, url, new { amount = 1_000_000, reason = "" }, HttpStatusCode.BadRequest);
        var tooMuch = await SendAsync(finance, url, new { amount = 3_000_001, reason = "مشتری انصراف داد" }, HttpStatusCode.BadRequest);
        tooMuch.GetProperty("errors").GetProperty("amount")[0].GetString().ShouldBe("billing.refund.too_much");

        var partial = await SendAsync(finance, url, new { amount = 1_000_000, reason = "بخشی برگشت داده شد", referenceId = "R-77" }, HttpStatusCode.OK);
        partial.GetProperty("payment").GetProperty("refunded").GetInt64().ShouldBe(1_000_000);
        partial.GetProperty("refunds")[0].GetProperty("referenceId").GetString().ShouldBe("R-77");
        partial.GetProperty("subscription").GetProperty("state").GetString().ShouldNotBe("ended");
        (await CurrentAsync(s)).GetProperty("planId").GetGuid().ShouldBe(plan);

        // The rest, ending the subscription: the workspace is back on the free plan.
        await SendAsync(finance, url, new { amount = 2_000_001, reason = "باقی مبلغ" }, HttpStatusCode.BadRequest);
        var full = await SendAsync(finance, url, new { amount = 2_000_000, reason = "باقی مبلغ", endSubscription = true }, HttpStatusCode.OK);
        full.GetProperty("refunds").GetArrayLength().ShouldBe(2);
        full.GetProperty("refunds")[1].GetProperty("endedSubscription").GetBoolean().ShouldBeTrue();
        full.GetProperty("subscription").GetProperty("endReason").GetString().ShouldBe("refunded");
        (await CurrentAsync(s)).GetProperty("source").GetString().ShouldNotBe("subscription");
        await SendAsync(finance, url, new { amount = 1, reason = "دوباره" }, HttpStatusCode.BadRequest);

        var detail = await reader.GetFromJsonAsync<JsonElement>($"/admin/v1/billing/payments/{checkoutId}", Ct);
        detail.GetProperty("payment").GetProperty("refunded").GetInt64().ShouldBe(3_000_000);
        (await reader.GetAsync($"/admin/v1/billing/payments/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // An unpaid checkout cannot be refunded.
        var (pendingId, _) = await StartAsync(s, plan);
        Code(await SendAsync(finance, $"/admin/v1/billing/payments/{pendingId}/refunds", new { amount = 1, reason = "پرداخت نشده" }, HttpStatusCode.Conflict))
            .ShouldBe("billing.refund.not_paid");

        var summary = await reader.GetFromJsonAsync<JsonElement>("/admin/v1/billing/summary?days=1", Ct);
        summary.GetProperty("paidCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        summary.GetProperty("refundedAmount").GetInt64().ShouldBeGreaterThanOrEqualTo(3_000_000);
        summary.GetProperty("netAmount").GetInt64().ShouldBe(summary.GetProperty("paidAmount").GetInt64() - summary.GetProperty("refundedAmount").GetInt64());
    }

    [Fact]
    public async Task Zarinpal_down_leaves_nothing_paid_and_without_a_merchant_checkout_is_unavailable()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        var basic = await PlanAsync("down-" + marker, 2_000_000, null);
        var s = await SellerAsync();

        FakeZarinpal.Down = true;
        try
        {
            var down = await SendAsync(s.Client, s.Url + "/checkout", new { planId = basic }, HttpStatusCode.UnprocessableEntity);
            Code(down).ShouldBe("billing.checkout.gateway_unavailable");
        }
        finally
        {
            FakeZarinpal.Down = false;
        }

        // Zarinpal unreachable while verifying: still pending, and the same callback works once it is back.
        var (id, authority) = await StartAsync(s, basic);
        FakeZarinpal.Pay(authority);
        FakeZarinpal.Down = true;
        try
        {
            (await CallbackAsync(authority, "OK")).Headers.Location!.ToString().ShouldEndWith("status=pending");
        }
        finally
        {
            FakeZarinpal.Down = false;
        }

        (await CallbackAsync(authority, "OK")).Headers.Location!.ToString().ShouldEndWith("status=paid");
        (await s.Client.GetFromJsonAsync<JsonElement>($"{s.Url}/checkouts/{id}", Ct)).GetProperty("status").GetString().ShouldBe("paid");

        await using var unset = new ApiFactory(infra)
        {
            TestServices = x => x.AddHttpClient<ZarinpalGateway>().ConfigurePrimaryHttpMessageHandler(() => new FakeZarinpal()),
        };
        var client = await unset.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        Code(await SendAsync(client, $"/api/v1/workspaces/{ws}/billing/checkout", new { planId = basic }, HttpStatusCode.UnprocessableEntity))
            .ShouldBe("billing.checkout.unavailable");
    }
}
