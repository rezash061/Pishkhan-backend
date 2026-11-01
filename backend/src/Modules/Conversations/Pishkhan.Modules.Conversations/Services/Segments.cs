using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>
/// A segment: contacts matching <c>all</c> or <c>any</c> of up to 20 conditions. Each condition is a <see cref="Field"/>, an <see cref="Op"/> and
/// (except for <c>exists</c>/<c>not_exists</c>) a <see cref="Value"/>; a custom field condition also names its <see cref="Key"/>.
/// <list type="bullet">
/// <item><c>tag</c>: <c>has</c>, <c>not_has</c> (value: tag name)</item>
/// <item><c>field</c> (with <c>key</c>): <c>eq</c>, <c>neq</c>, <c>contains</c>, <c>exists</c>, <c>not_exists</c>, <c>gt</c>, <c>lt</c> (numbers and ISO dates)</item>
/// <item><c>name</c>, <c>username</c>: <c>contains</c>, <c>exists</c>, <c>not_exists</c></item>
/// <item><c>channel_account</c>: <c>eq</c>, <c>neq</c> (value: channel account id)</item>
/// <item><c>last_seen</c>, <c>created</c>: <c>within_days</c>, <c>not_within_days</c> (value: 1 to 3650)</item>
/// <item><c>has_phone</c>: <c>is</c> (value: true or false)</item>
/// </list>
/// </summary>
public sealed record SegmentRule(string Match, IReadOnlyList<SegmentCondition> Conditions);

public sealed record SegmentCondition(string Field, string Op, string? Key = null, JsonElement? Value = null);

/// <summary>Validates a <see cref="SegmentRule"/> and turns it into a parameterised SQL filter over <c>conversations.contacts c</c>.</summary>
public static partial class SegmentSql
{
    public const int MaxConditions = 20;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    public static partial Regex FieldKey();

    /// <summary>Field errors keyed like <c>conditions[2].value</c>; empty when the rule is valid.</summary>
    public static Dictionary<string, string[]> Validate(SegmentRule? rule)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (rule is null)
        {
            errors["rule"] = ["validation.segment.required"];
            return errors;
        }

        if (rule.Match is not ("all" or "any"))
        {
            errors["rule.match"] = ["validation.segment.match"];
        }

        if (rule.Conditions is not { Count: > 0 and <= MaxConditions })
        {
            errors["rule.conditions"] = ["validation.segment.conditions"];
            return errors;
        }

        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            if (Check(rule.Conditions[i]) is { } error)
            {
                errors[$"rule.conditions[{i}].{error.Field}"] = [error.Code];
            }
        }

        return errors;
    }

    private static (string Field, string Code)? Check(SegmentCondition c)
    {
        string? Text() => c.Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : c.Value is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : null;
        bool NeedsText(int max) => Text() is { Length: > 0 } t && t.Length <= max;

        switch (c.Field)
        {
            case "tag" when c.Op is "has" or "not_has":
                return NeedsText(50) ? null : ("value", "validation.segment.value");
            case "field":
                if (c.Key is null || !FieldKey().IsMatch(c.Key))
                {
                    return ("key", "validation.segment.key");
                }

                return c.Op switch
                {
                    "exists" or "not_exists" => null,
                    "eq" or "neq" or "contains" => NeedsText(500) ? null : ("value", "validation.segment.value"),
                    "gt" or "lt" => Text() is { } t && (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out _) || DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        ? null : ("value", "validation.segment.value"),
                    _ => ("op", "validation.segment.op"),
                };
            case "name" or "username":
                return c.Op switch
                {
                    "exists" or "not_exists" => null,
                    "contains" => NeedsText(100) ? null : ("value", "validation.segment.value"),
                    _ => ("op", "validation.segment.op"),
                };
            case "channel_account" when c.Op is "eq" or "neq":
                return Guid.TryParse(Text(), out _) ? null : ("value", "validation.segment.value");
            case "last_seen" or "created" when c.Op is "within_days" or "not_within_days":
                return c.Value is { ValueKind: JsonValueKind.Number } d && d.TryGetInt32(out var days) && days is >= 1 and <= 3650 ? null : ("value", "validation.segment.value");
            case "has_phone" when c.Op == "is":
                return c.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } ? null : ("value", "validation.segment.value");
            case "tag" or "channel_account" or "last_seen" or "created" or "has_phone":
                return ("op", "validation.segment.op");
            default:
                return ("field", "validation.segment.field");
        }
    }

    /// <summary>A boolean SQL expression over alias <c>c</c>. Call only with a rule <see cref="Validate"/> accepted.</summary>
    public static string ToWhere(SegmentRule rule, List<NpgsqlParameter> parameters, DateTimeOffset now)
    {
        string P(object value, NpgsqlDbType? type = null)
        {
            var name = "s" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(type is { } t ? new NpgsqlParameter(name, t) { Value = value } : new NpgsqlParameter(name, value));
            return "@" + name;
        }

        var parts = new List<string>();
        foreach (var c in rule.Conditions)
        {
            var text = c.Value is { ValueKind: JsonValueKind.String } v ? v.GetString()! : c.Value?.GetRawText() ?? string.Empty;
            parts.Add((c.Field, c.Op) switch
            {
                ("tag", "has") => $"{P(text)} = ANY(c.tags)",
                ("tag", "not_has") => $"NOT ({P(text)} = ANY(c.tags))",
                ("field", "exists") => $"(c.fields ? {P(c.Key!)})",
                ("field", "not_exists") => $"NOT (c.fields ? {P(c.Key!)})",
                ("field", "eq") => $"(c.fields->>{P(c.Key!)}) = {P(text)}",
                ("field", "neq") => $"(c.fields->>{P(c.Key!)}) IS DISTINCT FROM {P(text)}",
                ("field", "contains") => $"(c.fields->>{P(c.Key!)}) ILIKE {P(Like(text))}",
                ("field", "gt" or "lt") => Compare(c, text, P),
                ("name", "contains") => $"c.display_name ILIKE {P(Like(text))}",
                ("name", "exists") => "c.display_name IS NOT NULL",
                ("name", "not_exists") => "c.display_name IS NULL",
                ("username", "contains") => $"c.username ILIKE {P(Like(text.TrimStart('@')))}",
                ("username", "exists") => "c.username IS NOT NULL",
                ("username", "not_exists") => "c.username IS NULL",
                ("channel_account", "eq") => $"EXISTS (SELECT 1 FROM conversations.contacts_channel_identities i WHERE i.contact_id = c.id AND i.channel_account_id = {P(Guid.Parse(text))})",
                ("channel_account", "neq") => $"NOT EXISTS (SELECT 1 FROM conversations.contacts_channel_identities i WHERE i.contact_id = c.id AND i.channel_account_id = {P(Guid.Parse(text))})",
                ("last_seen", "within_days") => $"EXISTS (SELECT 1 FROM conversations.conversations v WHERE v.contact_id = c.id AND v.last_inbound_at >= {P(Since(now, text))})",
                ("last_seen", "not_within_days") => $"NOT EXISTS (SELECT 1 FROM conversations.conversations v WHERE v.contact_id = c.id AND v.last_inbound_at >= {P(Since(now, text))})",
                ("created", "within_days") => $"c.created_at >= {P(Since(now, text))}",
                ("created", "not_within_days") => $"c.created_at < {P(Since(now, text))}",
                ("has_phone", "is") => c.Value!.Value.GetBoolean() ? "(c.fields ? 'phone')" : "NOT (c.fields ? 'phone')",
                _ => throw new InvalidOperationException($"Unvalidated segment condition {c.Field} {c.Op}"),
            });
        }

        var joiner = rule.Match == "any" ? " OR " : " AND ";
        return new StringBuilder("(").AppendJoin(joiner, parts).Append(')').ToString();
    }

    private static string Compare(SegmentCondition c, string text, Func<object, NpgsqlDbType?, string> p)
    {
        var op = c.Op == "gt" ? ">" : "<";
        var key = p(c.Key!, null);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            // Values that are not numbers are left out rather than failing the query.
            return $"(CASE WHEN (c.fields->>{key}) ~ '^-?[0-9]+(\\.[0-9]+)?$' THEN (c.fields->>{key})::numeric END) {op} {p(number, NpgsqlDbType.Numeric)}";
        }

        return $"(CASE WHEN (c.fields->>{key}) ~ '^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$' THEN (c.fields->>{key}) END) {op} {p(text, null)}";
    }

    private static DateTimeOffset Since(DateTimeOffset now, string days) => now.AddDays(-int.Parse(days, CultureInfo.InvariantCulture));

    public static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
