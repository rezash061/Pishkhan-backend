using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Pishkhan.Infrastructure.Telemetry;

/// <summary>
/// Masks personal data and secrets before anything is logged (backend plan 3.6): Iranian mobile numbers, 16-digit card numbers,
/// Sheba numbers, bearer tokens and long secret-looking values. Applied to the rendered message and every string property.
/// </summary>
public static partial class PiiRedactor
{
    [GeneratedRegex(@"(?<![\d۰-۹])(?:\+?98|0098|0)?9[\d۰-۹]{2}[\s-]?[\d۰-۹]{3}[\s-]?[\d۰-۹]{4}(?![\d۰-۹])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Mobile();

    [GeneratedRegex(@"(?<![\d۰-۹])[\d۰-۹]{4}[\s-]?[\d۰-۹]{4}[\s-]?[\d۰-۹]{4}[\s-]?[\d۰-۹]{4}(?![\d۰-۹])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Card();

    [GeneratedRegex(@"\bIR[\s]?[\d۰-۹]{2}(?:[\s]?[\d۰-۹]{2,4}){5,6}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex Sheba();

    [GeneratedRegex(@"(?i)(bearer\s+|access_token=|client_secret=|password=|token=|api[_-]?key=)[^\s&""']+", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"\b(?:IGAA|EAA)[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant, 250)]
    private static partial Regex MetaTokens();

    /// <summary>Kavenegar puts the API key in the URL path: <c>/v1/{key}/sms/send.json</c>.</summary>
    [GeneratedRegex(@"(api\.kavenegar\.com/v1/)[^/\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex KavenegarKey();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = Secrets().Replace(value, m => m.Groups[1].Value + "***");
        result = KavenegarKey().Replace(result, m => m.Groups[1].Value + "***");
        result = MetaTokens().Replace(result, "***token***");
        result = Sheba().Replace(result, "IR**-****");
        result = Card().Replace(result, m => "****-****-****-" + Tail(m.Value, 4));
        result = Mobile().Replace(result, m => "09**-***-" + Tail(m.Value, 2));
        return result;
    }

    private static string Tail(string value, int count)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= count ? Persian.PersianDigits.ToLatin(digits[^count..]) : string.Empty;
    }
}

/// <summary>Serilog enricher that replaces string property values with their redacted form.</summary>
public sealed class PiiRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = RedactValue(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string s } when PiiRedactor.Redact(s) is var r && !string.Equals(r, s, StringComparison.Ordinal) => new ScalarValue(r),
        SequenceValue seq => new SequenceValue(seq.Elements.Select(RedactValue)),
        StructureValue st => new StructureValue(st.Properties.Select(p => new LogEventProperty(p.Name, RedactValue(p.Value))), st.TypeTag),
        _ => value,
    };
}
