using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pishkhan.Modules.Payments.Services;

public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    /// <summary>
    /// Where payment pages are served: <c>{PublicBaseUrl}/{token}</c>, and the provider's callback <c>{PublicBaseUrl}/callback/{provider}</c>
    /// (production <c>https://pay.example.ir</c>, proxied to the Api host's <c>/pay</c>).
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080/pay";

    /// <summary>How long a payment link can be paid.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(3);

    public ZarinpalOptions Zarinpal { get; set; } = new();
}

public sealed class ZarinpalOptions
{
    public string BaseUrl { get; set; } = "https://payment.zarinpal.com";

    public string SandboxBaseUrl { get; set; } = "https://sandbox.zarinpal.com";
}

/// <summary>A seller's merchant account, decrypted for one call.</summary>
internal sealed record GatewayMerchant(string MerchantId, bool Sandbox);

/// <summary>A started payment: the provider's authority and where to send the customer, or the provider's error code.</summary>
internal sealed record GatewayStart(string? Authority, Uri? RedirectUrl, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>A verification: <see cref="Ok"/> when the provider confirmed the payment of exactly the amount we asked for.</summary>
internal sealed record GatewayVerify(bool Ok, string Code, string? ReferenceId, string? CardMask);

/// <summary>A payment provider (backend plan 9.10). Amounts are Rial.</summary>
internal interface IPaymentGateway
{
    string Provider { get; }

    Task<GatewayStart> StartAsync(GatewayMerchant merchant, long amountRial, Uri callback, string description, CancellationToken ct);

    Task<GatewayVerify> VerifyAsync(GatewayMerchant merchant, long amountRial, string authority, CancellationToken ct);
}

/// <summary>
/// Zarinpal REST API v4: <c>/pg/v4/payment/request.json</c> gives an authority and the customer goes to <c>/pg/StartPay/{authority}</c>;
/// the callback brings the authority back and <c>/pg/v4/payment/verify.json</c> confirms it for our amount. Code 100 is paid, 101 is
/// paid and already verified. Amounts are sent in Rial (<c>currency: IRR</c>).
/// </summary>
internal sealed class ZarinpalGateway(HttpClient http, IOptions<PaymentsOptions> options, ILogger<ZarinpalGateway> logger) : IPaymentGateway
{
    public string Provider => Contracts.PaymentProviders.Zarinpal;

    public async Task<GatewayStart> StartAsync(GatewayMerchant merchant, long amountRial, Uri callback, string description, CancellationToken ct)
    {
        var body = new { merchant_id = merchant.MerchantId, amount = amountRial, currency = "IRR", callback_url = callback.ToString(), description };
        var (data, error) = await PostAsync(merchant, "/pg/v4/payment/request.json", body, ct);
        if (data is not { } d || Code(d) != 100 || d.TryGetProperty("authority", out var a) is false || a.GetString() is not { Length: > 0 } authority)
        {
            return new GatewayStart(null, null, error ?? (data is { } x ? Code(x).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable"));
        }

        return new GatewayStart(authority, new Uri($"{Base(merchant)}/pg/StartPay/{Uri.EscapeDataString(authority)}"), null);
    }

    public async Task<GatewayVerify> VerifyAsync(GatewayMerchant merchant, long amountRial, string authority, CancellationToken ct)
    {
        var body = new { merchant_id = merchant.MerchantId, amount = amountRial, authority };
        var (data, error) = await PostAsync(merchant, "/pg/v4/payment/verify.json", body, ct);
        if (data is not { } d)
        {
            return new GatewayVerify(false, error ?? "unavailable", null, null);
        }

        var code = Code(d);
        if (code is not (100 or 101))
        {
            return new GatewayVerify(false, code.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null);
        }

        var reference = d.TryGetProperty("ref_id", out var r) ? r.ValueKind == JsonValueKind.Number ? r.GetRawText() : r.GetString() : null;
        var card = d.TryGetProperty("card_pan", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return reference is { Length: > 0 }
            ? new GatewayVerify(true, code.ToString(System.Globalization.CultureInfo.InvariantCulture), reference, card)
            : new GatewayVerify(false, "no_reference", null, null);
    }

    private string Base(GatewayMerchant merchant) => (merchant.Sandbox ? options.Value.Zarinpal.SandboxBaseUrl : options.Value.Zarinpal.BaseUrl).TrimEnd('/');

    private static int Code(JsonElement data) => data.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : 0;

    /// <summary>The response's <c>data</c> object, or the error code (<c>errors.code</c>, or <c>unavailable</c> when Zarinpal could not be reached).</summary>
    private async Task<(JsonElement? Data, string? Error)> PostAsync(GatewayMerchant merchant, string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(Base(merchant) + path, body, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out _))
            {
                return (data.Clone(), null);
            }

            var code = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.TryGetProperty("code", out var e) ? e.GetRawText() : "invalid_response";
            return (null, code);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Zarinpal {Path} failed: {Error}", path, ex.GetType().Name);
            return (null, "unavailable");
        }
    }
}

internal sealed class ZarinpalClient(ZarinpalGateway gateway) : Contracts.IZarinpalClient
{
    public async Task<Contracts.ZarinpalStart> StartAsync(string merchantId, bool sandbox, long amountRial, Uri callback, string description, CancellationToken cancellationToken)
    {
        var started = await gateway.StartAsync(new GatewayMerchant(merchantId, sandbox), amountRial, callback, description, cancellationToken);
        return new Contracts.ZarinpalStart(started.Authority, started.RedirectUrl, started.Error);
    }

    public async Task<Contracts.ZarinpalVerify> VerifyAsync(string merchantId, bool sandbox, long amountRial, string authority, CancellationToken cancellationToken)
    {
        var verified = await gateway.VerifyAsync(new GatewayMerchant(merchantId, sandbox), amountRial, authority, cancellationToken);
        return new Contracts.ZarinpalVerify(verified.Ok, verified.Code, verified.ReferenceId, verified.CardMask);
    }
}
