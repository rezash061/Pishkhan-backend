using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Services;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Http;

namespace Pishkhan.Modules.Payments.Features;

/// <summary>
/// The customer's payment page (<c>/pay/{token}</c>): a small Persian page with the order summary and a pay button, served by the Api
/// host and proxied from the payment domain. The button posts back here and we send the customer to the provider; the provider's callback
/// (<c>/pay/callback/{provider}</c>) verifies the payment and returns the customer to the page, which then shows the result.
/// </summary>
internal static class PayPages
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var pay = app.MapGroup("/pay").AllowAnonymous().ExcludeFromDescription().RequireRateLimiting(RateLimitPolicies.Public);
        pay.MapGet("/{token}", ShowAsync);
        pay.MapPost("/{token}", StartAsync).DisableAntiforgery();
        pay.MapGet("/callback/{provider}", CallbackAsync);
    }

    private static async Task<IResult> ShowAsync(string token, HttpContext http, PaymentFlow flow, IOptions<PaymentsOptions> options, IClock clock)
    {
        var link = await flow.FindByTokenAsync(token, http.RequestAborted);
        return Page(http, options.Value, link, clock.UtcNow, link?.LastError is { } e && link.Status == PaymentLinkStatuses.Active ? Retry(e) : null);
    }

    private static async Task<IResult> StartAsync(string token, HttpContext http, PaymentFlow flow, IOptions<PaymentsOptions> options, IClock clock)
    {
        var started = await flow.StartAsync(token, http.RequestAborted);
        if (started.Redirect is { } redirect)
        {
            return SeeOther(http, redirect.ToString());
        }

        var link = await flow.FindByTokenAsync(token, http.RequestAborted);
        var message = started.Error switch
        {
            "too_many_attempts" => "تعداد تلاش‌های پرداخت این لینک تمام شده است. لطفاً از فروشنده لینک تازه بخواهید.",
            "merchant_unavailable" => "درگاه پرداخت این فروشگاه در حال حاضر فعال نیست. لطفاً با فروشنده تماس بگیرید.",
            "gateway_unavailable" => "اتصال به درگاه پرداخت برقرار نشد. چند دقیقه دیگر دوباره تلاش کنید.",
            _ => null,
        };
        return Page(http, options.Value, link, clock.UtcNow, message);
    }

    /// <summary>Zarinpal returns <c>?Authority=…&amp;Status=OK|NOK</c>. Status only says whether the customer finished; we verify either way it says OK.</summary>
    private static async Task<IResult> CallbackAsync(string provider, HttpContext http, PaymentFlow flow, IOptions<PaymentsOptions> options)
    {
        var q = http.Request.Query;
        var token = await flow.CompleteAsync(provider, q["Authority"].ToString(), string.Equals(q["Status"], "OK", StringComparison.Ordinal), http.RequestAborted);
        return token is null ? NotFoundPage(http, options.Value) : SeeOther(http, $"{options.Value.PublicBaseUrl.TrimEnd('/')}/{token}");
    }

    private static IResult SeeOther(HttpContext http, string location)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static string Retry(string code) => code == "cancelled"
        ? "پرداخت انجام نشد. اگر مایلید، دوباره تلاش کنید."
        : "پرداخت قبلی تأیید نشد و مبلغی از حساب شما برداشت نشده است (اگر برداشت شده باشد، طی ۷۲ ساعت برمی‌گردد). دوباره تلاش کنید.";

    private static IResult NotFoundPage(HttpContext http, PaymentsOptions options) => Html(http, options, StatusCodes.Status404NotFound, "لینک پرداخت پیدا نشد",
        "<p>این لینک پرداخت معتبر نیست. لطفاً لینک را از فروشنده دوباره بگیرید.</p>");

    private static IResult Page(HttpContext http, PaymentsOptions options, PaymentLink? link, DateTimeOffset now, string? message)
    {
        if (link is null)
        {
            return NotFoundPage(http, options);
        }

        var body = new StringBuilder();
        body.Append("<p class=\"muted\">").Append(E(link.ShopName ?? "فروشگاه")).Append(" · سفارش ").Append(E(PersianDigits.ToPersian(link.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Append("</p>");
        body.Append("<table>");
        foreach (var line in link.Lines)
        {
            var title = line.VariantLabel is { Length: > 0 } v ? $"{line.Title} ({v})" : line.Title;
            body.Append("<tr><td>").Append(E(title)).Append(" × ").Append(E(PersianDigits.ToPersian(line.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                .Append("</td><td class=\"num\">").Append(E(PersianMoney.FormatToman(Money.Rial(line.LineTotal)))).Append("</td></tr>");
        }

        body.Append("<tr class=\"total\"><td>مبلغ قابل پرداخت</td><td class=\"num\">").Append(E(PersianMoney.FormatToman(Money.Rial(link.Amount)))).Append("</td></tr></table>");

        switch (link.StatusAt(now))
        {
            case PaymentLinkStatuses.Paid:
                body.Append("<div class=\"ok\">پرداخت با موفقیت انجام شد.");
                if (link.ReferenceId is { } reference)
                {
                    body.Append("<br>کد پیگیری: <bdi>").Append(E(PersianDigits.ToPersian(reference))).Append("</bdi>");
                }

                body.Append("</div>");
                return Html(http, options, StatusCodes.Status200OK, "پرداخت انجام شد", body.ToString());
            case PaymentLinkStatuses.Active:
                if (message is not null)
                {
                    body.Append("<div class=\"warn\">").Append(E(message)).Append("</div>");
                }

                body.Append("<form method=\"post\"><button type=\"submit\">پرداخت آنلاین</button></form>");
                body.Append("<p class=\"muted small\">پرداخت از طریق درگاه امن زرین‌پال انجام می‌شود.</p>");
                return Html(http, options, StatusCodes.Status200OK, "پرداخت سفارش", body.ToString());
            default:
                body.Append("<div class=\"warn\">این لینک پرداخت دیگر فعال نیست. لطفاً از فروشنده لینک تازه بخواهید.</div>");
                return Html(http, options, StatusCodes.Status200OK, "لینک غیرفعال", body.ToString());
        }
    }

    private const string Css =
        "body{margin:0;background:#f5f6f8;color:#1c1f24;font-family:Vazirmatn,Tahoma,sans-serif;line-height:1.7}" +
        "main{max-width:440px;margin:24px auto;padding:20px 16px;background:#fff;border-radius:14px;box-shadow:0 1px 3px rgba(0,0,0,.08)}" +
        "h1{font-size:1.2rem;margin:0 0 4px}" +
        "table{width:100%;border-collapse:collapse;margin:12px 0}" +
        "td{padding:8px 0;border-bottom:1px solid #eceef1;vertical-align:top}" +
        ".num{text-align:left;white-space:nowrap;padding-right:12px}" +
        ".total td{font-weight:700;border-bottom:0}" +
        ".muted{color:#6b7280}.small{font-size:.85rem}" +
        ".ok{background:#e8f7ee;color:#14532d;padding:12px;border-radius:10px}" +
        ".warn{background:#fff6e5;color:#7a4b00;padding:12px;border-radius:10px;margin-bottom:12px}" +
        "button{width:100%;padding:14px;font:inherit;font-weight:700;color:#fff;background:#2563eb;border:0;border-radius:10px;cursor:pointer}" +
        "@media (prefers-color-scheme:dark){body{background:#111317;color:#e5e7eb}main{background:#1b1e24;box-shadow:none}td{border-color:#2a2e36}.muted{color:#9ca3af}.ok{background:#12301f;color:#bbf7d0}.warn{background:#3a2a0a;color:#fde68a}}";

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static IResult Html(HttpContext http, PaymentsOptions options, int status, string title, string body)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        // The form posts here and we redirect to the provider; form-action covers that redirect, so the provider's hosts are allowed too.
        headers.ContentSecurityPolicy =
            $"default-src 'none'; style-src 'unsafe-inline'; form-action 'self' {Origin(options.Zarinpal.BaseUrl)} {Origin(options.Zarinpal.SandboxBaseUrl)}; frame-ancestors 'none'; base-uri 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        var html = $$"""
            <!doctype html>
            <html lang="fa" dir="rtl">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{E(title)}}</title>
            <style>
            {{Css}}
            </style>
            </head>
            <body><main><h1>{{E(title)}}</h1>{{body}}</main></body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : string.Empty;
}
