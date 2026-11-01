namespace Pishkhan.Modules.Agent.Domain;

/// <summary>An OpenAI-compatible provider set in the back office (SuperAdmin). Overrides a configured provider of the same name.</summary>
public sealed class AiProvider
{
    public required string Name { get; init; }

    /// <summary><c>openai</c> (any OpenAI-compatible API) or <c>fake</c>.</summary>
    public required string Kind { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>Encrypted (secret protector); never returned by the API.</summary>
    public string? ApiKeyEnc { get; set; }

    /// <summary>A disabled provider is left out of the settings, so its routes fall through to the next one.</summary>
    public bool Enabled { get; set; } = true;

    public string? LastTestError { get; set; }

    public DateTimeOffset? LastTestedAt { get; set; }

    public Guid UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One step of a task's fallback chain (AIOps). A task with rows here ignores its configured routes.</summary>
public sealed class AiRoute
{
    public Guid Id { get; init; }

    public required string Task { get; init; }

    public int Position { get; init; }

    public required string Provider { get; init; }

    public required string Model { get; init; }

    public float? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    public int TimeoutSeconds { get; init; }

    /// <summary>Rial per million tokens, for cost metering.</summary>
    public decimal? InputPricePerMillion { get; init; }

    public decimal? OutputPricePerMillion { get; init; }

    public Guid UpdatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A prompt version written in the back office. Versions are numbered per key after the configured ones.</summary>
public sealed class PromptVersion
{
    public Guid Id { get; init; }

    public required string Key { get; init; }

    public required string Version { get; init; }

    public required string Text { get; init; }

    /// <summary>The version it was edited from, for the diff view.</summary>
    public string? BaseVersion { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The active version of a prompt key, set only through the eval gate. Overrides <c>Ai:ActivePrompts</c>.</summary>
public sealed class PromptActivation
{
    public required string Key { get; init; }

    public required string Version { get; set; }

    public Guid EvalRunId { get; set; }

    public Guid ActivatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
