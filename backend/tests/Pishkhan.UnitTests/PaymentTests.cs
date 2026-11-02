using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Services;

namespace Pishkhan.UnitTests;

public class PaymentLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static PaymentLink Link() =>
        PaymentLink.Create(Guid.NewGuid(), Guid.NewGuid(), 12, "شاپ", [new PaymentLinkLine("کیف", null, 1, 10_000)], 10_000, "zarinpal", TimeSpan.FromDays(3), null, Now);

    [Fact]
    public void Tokens_are_long_random_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => PaymentLink.NewToken()).ToList();
        tokens.Distinct(StringComparer.Ordinal).Count().ShouldBe(50);
        tokens.ShouldAllBe(t => t.Length == 32 && t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
    }

    [Fact]
    public void A_link_expires_and_runs_out_of_attempts()
    {
        var link = Link();
        link.CanStart(Now).ShouldBeTrue();
        link.StatusAt(Now.AddDays(3)).ShouldBe(PaymentLinkStatuses.Expired);
        link.CanStart(Now.AddDays(3)).ShouldBeFalse();

        for (var i = 0; i < PaymentLink.MaxAttempts; i++)
        {
            link.Started(Now);
        }

        link.CanStart(Now).ShouldBeFalse();
    }

    [Fact]
    public void A_link_is_paid_once_and_a_verified_payment_counts_even_after_a_cancel()
    {
        var link = Link();
        link.Cancel(Now).ShouldBeTrue();
        link.Cancel(Now).ShouldBeFalse();
        link.CanStart(Now).ShouldBeFalse();

        link.MarkPaid("201", "5022****", Now).ShouldBeTrue();
        link.Status.ShouldBe(PaymentLinkStatuses.Paid);
        link.MarkPaid("202", null, Now).ShouldBeFalse();
        link.ReferenceId.ShouldBe("201");
        link.Cancel(Now).ShouldBeFalse();
    }
}

public class ZarinpalGatewayTests
{
    private static readonly GatewayMerchant Merchant = new("11111111-2222-3333-4444-555555555555", Sandbox: false);

    private sealed class Stub(Func<string, string, string?> respond) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(JsonDocument.Parse(body).RootElement.Clone());
            return respond(request.RequestUri!.ToString(), body) is { } json
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : throw new HttpRequestException("down");
        }
    }

    private static (ZarinpalGateway Gateway, Stub Stub) Create(Func<string, string, string?> respond)
    {
        var stub = new Stub(respond);
        return (new ZarinpalGateway(new HttpClient(stub), Options.Create(new PaymentsOptions()), NullLogger<ZarinpalGateway>.Instance), stub);
    }

    [Fact]
    public async Task Start_sends_rial_and_redirects_to_start_pay()
    {
        var (gateway, stub) = Create((url, _) =>
        {
            url.ShouldBe("https://payment.zarinpal.com/pg/v4/payment/request.json");
            return """{"data":{"code":100,"message":"Success","authority":"A00000000000000000000000000217885159"},"errors":[]}""";
        });

        var started = await gateway.StartAsync(Merchant, 24_500_000, new Uri("https://pay.example.ir/callback/zarinpal"), "سفارش ۱۲", CancellationToken.None);

        started.Ok.ShouldBeTrue();
        started.RedirectUrl!.ToString().ShouldBe("https://payment.zarinpal.com/pg/StartPay/A00000000000000000000000000217885159");
        stub.Bodies[0].GetProperty("amount").GetInt64().ShouldBe(24_500_000);
        stub.Bodies[0].GetProperty("currency").GetString().ShouldBe("IRR");
    }

    [Fact]
    public async Task Sandbox_merchants_use_the_sandbox()
    {
        var (gateway, _) = Create((url, _) =>
        {
            url.ShouldStartWith("https://sandbox.zarinpal.com/");
            return """{"data":{"code":100,"authority":"S1"},"errors":[]}""";
        });

        var started = await gateway.StartAsync(Merchant with { Sandbox = true }, 10_000, new Uri("https://pay.example.ir/callback/zarinpal"), "x", CancellationToken.None);
        started.RedirectUrl!.Host.ShouldBe("sandbox.zarinpal.com");
    }

    [Theory]
    [InlineData("""{"data":{"code":100,"ref_id":201,"card_pan":"502229******5995"},"errors":[]}""", true, "100", "201")]
    [InlineData("""{"data":{"code":101,"ref_id":"201"},"errors":[]}""", true, "101", "201")]
    [InlineData("""{"data":[],"errors":{"code":-50,"message":"Session is not valid, amounts values is not the same."}}""", false, "-50", null)]
    [InlineData("""{"data":[],"errors":{"code":-51,"message":"Session is not valid, session is not active paid try."}}""", false, "-51", null)]
    [InlineData("""{"data":{"code":100},"errors":[]}""", false, "no_reference", null)]
    [InlineData("<html>bad gateway</html>", false, "unavailable", null)]
    [InlineData(null, false, "unavailable", null)]
    public async Task Verify_reads_zarinpal_answers(string? answer, bool ok, string code, string? reference)
    {
        var (gateway, stub) = Create((_, _) => answer);

        var verified = await gateway.VerifyAsync(Merchant, 24_500_000, "A1", CancellationToken.None);

        verified.Ok.ShouldBe(ok);
        verified.Code.ShouldBe(code);
        verified.ReferenceId.ShouldBe(reference);
        stub.Bodies[0].GetProperty("amount").GetInt64().ShouldBe(24_500_000);
    }
}
