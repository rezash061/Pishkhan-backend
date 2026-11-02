using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Domain;

public static class PriceChangeKinds
{
    /// <summary>By a percentage of each price, for example <c>10</c> or <c>-15</c>.</summary>
    public const string Percent = "percent";

    /// <summary>By a fixed amount added to (or, negative, taken off) each price.</summary>
    public const string Amount = "amount";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Percent, Amount };
}

public static class RoundingModes
{
    public const string Nearest = "nearest";
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Nearest, Up, Down };
}

/// <summary>
/// How every price changes. <see cref="Value"/> is the percentage for <see cref="PriceChangeKinds.Percent"/> and Rial for
/// <see cref="PriceChangeKinds.Amount"/>. New prices are rounded to <see cref="RoundToToman"/> Toman (1 rounds to whole Toman).
/// </summary>
public sealed record PriceRule(string Kind, decimal Value, long RoundToToman, string RoundMode)
{
    public const decimal MinPercent = -90;
    public const decimal MaxPercent = 500;

    /// <summary>The rounding steps sellers pick from, in Toman.</summary>
    public static readonly IReadOnlySet<long> RoundingSteps = new HashSet<long> { 1, 100, 500, 1_000, 5_000, 10_000, 50_000, 100_000 };

    /// <summary>The new price in Rial, or null when it would be zero or less.</summary>
    public long? Apply(long rial)
    {
        var raw = Kind == PriceChangeKinds.Percent ? rial * (1 + (Value / 100m)) : rial + Value;
        var step = RoundToToman * 10m;
        var steps = raw / step;
        var rounded = RoundMode switch
        {
            RoundingModes.Up => decimal.Ceiling(steps),
            RoundingModes.Down => decimal.Floor(steps),
            _ => decimal.Round(steps, MidpointRounding.AwayFromZero),
        };
        var result = rounded * step;
        return result <= 0 || result > long.MaxValue / 2 ? null : (long)result;
    }
}

/// <summary>One price in a batch: the product's own price (<see cref="VariantId"/> null) or a variant's own price. Rial.</summary>
public sealed record PriceChangeItem(Guid ProductId, Guid? VariantId, long OldPrice, long NewPrice);

/// <summary>What a batch was asked for, kept to show it in the history.</summary>
public sealed record PriceChangeScope(IReadOnlyList<Guid>? ProductIds, Guid? CategoryId, string? Tag, string? Status, bool All);

/// <summary>
/// One bulk price update (backend plan 9.8): every price it changed, before and after, so it can be undone within
/// <see cref="UndoWindow"/>. Undo only restores prices nobody has changed since.
/// </summary>
public sealed class PriceChangeBatch : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxItems = 5_000;
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    private PriceChangeBatch()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public PriceChangeScope Scope { get; private set; } = new(null, null, null, null, false);

    public PriceRule Rule { get; private set; } = new(PriceChangeKinds.Percent, 0, 1, RoundingModes.Nearest);

    public List<PriceChangeItem> Items { get; private set; } = [];

    public int ProductCount { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UndoneAt { get; private set; }

    public Guid? UndoneBy { get; private set; }

    /// <summary>Prices undo left alone because they had been changed again after the batch.</summary>
    public int UndoSkipped { get; private set; }

    public static PriceChangeBatch Create(Guid workspaceId, PriceChangeScope scope, PriceRule rule, IReadOnlyList<PriceChangeItem> items, Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            Id = Uuid.NewV7(now), WorkspaceId = workspaceId, Scope = scope, Rule = rule, Items = [.. items], ProductCount = items.Select(i => i.ProductId).Distinct().Count(),
            CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };

    public bool CanUndo(DateTimeOffset now) => UndoneAt is null && now - CreatedAt <= UndoWindow;

    public void MarkUndone(Guid? userId, int skipped, DateTimeOffset now)
    {
        UndoneAt = now;
        UndoneBy = userId;
        UndoSkipped = skipped;
        UpdatedAt = now;
    }
}
