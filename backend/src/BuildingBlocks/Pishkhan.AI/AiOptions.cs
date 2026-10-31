namespace Pishkhan.AI;

/// <summary>Tasks the platform asks models to do (backend plan 10.1). Routes are configured per task.</summary>
public static class AiTasks
{
    public const string Classify = "classify";
    public const string GenerateReply = "generate_reply";
    public const string ExtractReceipt = "extract_receipt";
    public const string Transcribe = "transcribe";
    public const string Embed = "embed";
    public const string Summarize = "summarize";
    public const string Content = "content";
}

/// <summary>
/// The AI configuration in effect now. The default reads <see cref="AiOptions"/> from configuration; the Agent module replaces it
/// with configuration merged with what staff set in the back office (providers, routes, prompts), reloaded without a restart.
/// </summary>
public interface IAiSettings
{
    AiOptions Current { get; }
}

internal sealed class ConfigAiSettings(Microsoft.Extensions.Options.IOptionsMonitor<AiOptions> options) : IAiSettings
{
    public AiOptions Current => options.CurrentValue;
}

/// <summary>
/// Model gateway configuration (section <c>Ai</c>). Read through <c>IOptionsMonitor</c>, so changing a route in configuration
/// switches the model without a code change or restart. Backoffice's <c>ai_model_routes</c> will feed the same shape in Phase 1.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>OpenAI-compatible endpoints by name (assumption A3: permitted hosted providers or self-hosted models).</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Fallback chain per task: the first route is tried first.</summary>
    public Dictionary<string, List<AiRouteOptions>> Routes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Price per million tokens in Rial, by <c>provider/model</c>, for cost metering.</summary>
    public Dictionary<string, AiPrice> Prices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Prompt templates: key → version → text. The active version per key is in <see cref="ActivePrompts"/>.</summary>
    public Dictionary<string, Dictionary<string, string>> Prompts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> ActivePrompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Consecutive failures that open a provider's circuit, and how long it stays open.</summary>
    public int CircuitBreakerFailures { get; set; } = 5;

    public int CircuitBreakerSeconds { get; set; } = 30;

    /// <summary>What an eval run must reach before its prompt version can be activated (admin plan 4.5).</summary>
    public AiEvalGateOptions EvalGate { get; set; } = new();
}

public sealed class AiEvalGateOptions
{
    public decimal MinIntentAccuracy { get; set; } = 0.85m;

    public decimal MaxErrorRate { get; set; } = 0.05m;

    public int MinCases { get; set; } = 1;
}

public sealed class AiProviderOptions
{
    /// <summary><c>openai</c> (any OpenAI-compatible API) or <c>fake</c> (deterministic, for development, tests and dummy eval runs).</summary>
    public string Kind { get; set; } = "openai";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class AiRouteOptions
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public float? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string Key => $"{Provider}/{Model}";
}

public sealed class AiPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}
