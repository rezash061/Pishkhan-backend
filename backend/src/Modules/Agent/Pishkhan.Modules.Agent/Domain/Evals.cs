namespace Pishkhan.Modules.Agent.Domain;

/// <summary>A named, versioned set of anonymized and consented customer messages with expected outcomes (backend plan 10.4).</summary>
public sealed class EvalSet
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EvalCase
{
    public Guid Id { get; init; }

    public Guid SetId { get; init; }

    /// <summary>Stable id from the import file, so re-importing updates instead of duplicating.</summary>
    public required string ExternalRef { get; init; }

    public string? Vertical { get; set; }

    /// <summary>The customer message under test (the last customer turn).</summary>
    public required string Input { get; set; }

    /// <summary>Earlier turns as JSON, for context-dependent cases.</summary>
    public string? ContextJson { get; set; }

    public string? ExpectedIntent { get; set; }

    public bool? ExpectedHandoff { get; set; }

    public List<string> ExpectedFacts { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EvalRun
{
    public Guid Id { get; init; }

    public Guid SetId { get; init; }

    public required string Task { get; init; }

    public required string PromptKey { get; init; }

    public required string PromptVersion { get; init; }

    /// <summary>The <c>provider/model</c> that answered most cases.</summary>
    public string? Route { get; set; }

    public string Status { get; set; } = "running";

    public int CaseCount { get; set; }

    public int ErrorCount { get; set; }

    public decimal? IntentAccuracy { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class EvalResult
{
    public Guid Id { get; init; }

    public Guid RunId { get; init; }

    public Guid CaseId { get; init; }

    public string? Output { get; init; }

    public string? PredictedIntent { get; init; }

    public bool? IntentCorrect { get; init; }

    public string? Route { get; init; }

    public int LatencyMs { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; set; }
}
