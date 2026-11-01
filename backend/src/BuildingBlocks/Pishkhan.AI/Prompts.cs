using System.Text;
using System.Text.RegularExpressions;

namespace Pishkhan.AI;

public sealed record PromptTemplate(string Key, string Version, string Text)
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([a-z0-9_]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Replaces <c>{{name}}</c> placeholders. Unknown placeholders are an error so a typo never reaches a model silently.</summary>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        var missing = new StringBuilder();
        var result = Placeholder.Replace(Text, m =>
        {
            if (values.TryGetValue(m.Groups[1].Value, out var v))
            {
                return v;
            }

            missing.Append(m.Groups[1].Value).Append(' ');
            return m.Value;
        });
        return missing.Length == 0 ? result : throw new InvalidOperationException($"Prompt {Key}@{Version} is missing values: {missing}");
    }
}

/// <summary>Versioned prompts referred to by key. Config-backed in M0; <c>backoffice.prompt_templates</c> with the eval gate in Phase 1.</summary>
public interface IPromptRegistry
{
    PromptTemplate Get(string key, string? version = null);
}

internal sealed class ConfigPromptRegistry(IAiSettings settings) : IPromptRegistry
{
    public PromptTemplate Get(string key, string? version = null)
    {
        var o = settings.Current;
        if (!o.Prompts.TryGetValue(key, out var versions) || versions.Count == 0)
        {
            throw new KeyNotFoundException($"Prompt '{key}' is not registered.");
        }

        var v = version ?? (o.ActivePrompts.TryGetValue(key, out var active) ? active : versions.Keys.Max(StringComparer.Ordinal)!);
        return versions.TryGetValue(v, out var text) ? new PromptTemplate(key, v, text) : throw new KeyNotFoundException($"Prompt '{key}' has no version '{v}'.");
    }
}
