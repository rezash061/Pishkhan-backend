using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// Zarinpal's REST API v4 in memory. A payment is only verified after the test says the customer paid (<see cref="Pay"/>), and only for
/// the amount it was opened with; anything else answers like Zarinpal does (-51 not paid, -50 wrong amount, -54 unknown authority).
/// </summary>
internal sealed class FakeZarinpal : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<string, Payment> Payments = new(StringComparer.Ordinal);

    /// <summary>When set, every call fails as if Zarinpal were down.</summary>
    public static volatile bool Down;

    public sealed class Payment(string merchantId, long amount, string callback)
    {
        public string MerchantId { get; } = merchantId;

        public long Amount { get; } = amount;

        public string Callback { get; } = callback;

        public bool Paid { get; set; }

        public int Verifications;

        public long? VerifiedAmount { get; set; }
    }

    public static Payment Get(string authority) => Payments[authority];

    /// <summary>The customer completed the payment at the gateway.</summary>
    public static void Pay(string authority) => Payments[authority].Paid = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("connection refused");
        }

        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
        var merchant = body.GetProperty("merchant_id").GetString()!;
        var amount = body.GetProperty("amount").GetInt64();
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/pg/v4/payment/request.json":
                if (!Guid.TryParse(merchant, out _))
                {
                    return Error(-9);
                }

                body.GetProperty("currency").GetString().ShouldBe("IRR");
                var authority = "A000" + Guid.NewGuid().ToString("N");
                Payments[authority] = new Payment(merchant, amount, body.GetProperty("callback_url").GetString()!);
                return Json($$"""{"data":{"code":100,"message":"Success","authority":"{{authority}}","fee_type":"Merchant","fee":0},"errors":[]}""");
            case "/pg/v4/payment/verify.json":
                if (!Payments.TryGetValue(body.GetProperty("authority").GetString()!, out var payment) || payment.MerchantId != merchant)
                {
                    return Error(-54);
                }

                Interlocked.Increment(ref payment.Verifications);
                payment.VerifiedAmount = amount;
                if (amount != payment.Amount)
                {
                    return Error(-50);
                }

                if (!payment.Paid)
                {
                    return Error(-51);
                }

                var code = payment.Verifications > 1 ? 101 : 100;
                return Json($$"""{"data":{"code":{{code}},"message":"Verified","card_hash":"x","card_pan":"502229******5995","ref_id":201,"fee_type":"Merchant","fee":0},"errors":[]}""");
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Error(int code) =>
        Json($$$"""{"data":[],"errors":{"code":{{{code}}},"message":"error","validations":[]}}""");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
