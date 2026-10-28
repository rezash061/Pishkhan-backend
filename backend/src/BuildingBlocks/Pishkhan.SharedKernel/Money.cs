using System.Globalization;

namespace Pishkhan.SharedKernel;

/// <summary>ISO 4217 currency. Minor unit for IRR is the Rial itself (no decimals).</summary>
public readonly record struct Currency
{
    private Currency(string code, int minorUnitDigits)
    {
        Code = code;
        MinorUnitDigits = minorUnitDigits;
    }

    public string Code { get; }

    public int MinorUnitDigits { get; }

    public static readonly Currency Irr = new("IRR", 0);

    public static Currency FromCode(string code) => code.ToUpperInvariant() switch
    {
        "IRR" => Irr,
        _ => throw new DomainException("money.currency.unsupported", "Unsupported currency " + code),
    };

    public override string ToString() => Code;
}

/// <summary>
/// Money as an integer amount in the minor unit (Rial for IRR). Never use floating point for money.
/// Display in Toman is a presentation concern: <see cref="ToToman"/>.
/// </summary>
public readonly record struct Money(long Amount, Currency Currency)
{
    public static Money Rial(long amount) => new(amount, Currency.Irr);

    public static Money Toman(long toman) => new(checked(toman * 10), Currency.Irr);

    public static Money Zero(Currency currency) => new(0, currency);

    public static Money operator +(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a with { Amount = checked(a.Amount + b.Amount) };
    }

    public static Money operator -(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a with { Amount = checked(a.Amount - b.Amount) };
    }

    public static Money operator *(Money a, int factor) => a with { Amount = checked(a.Amount * factor) };

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Multiply(int factor) => this * factor;

    public bool IsNegative => Amount < 0;

    /// <summary>
    /// The single place where Rial is turned into Toman for display. Rial amounts not divisible by 10
    /// are rounded half away from zero, which is how Iranian shops quote Toman.
    /// </summary>
    public long ToToman()
    {
        if (Currency != Currency.Irr)
        {
            throw new DomainException("money.currency.not_irr");
        }

        return (long)Math.Round(Amount / 10m, MidpointRounding.AwayFromZero);
    }

    /// <summary>Rounds the amount to the nearest multiple of <paramref name="tomanStep"/> Toman (for example 1,000 Toman).</summary>
    public Money RoundToToman(long tomanStep)
    {
        if (tomanStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tomanStep));
        }

        var step = tomanStep * 10m;
        var rounded = Math.Round(Amount / step, MidpointRounding.AwayFromZero) * step;
        return this with { Amount = (long)rounded };
    }

    public override string ToString() => Amount.ToString(CultureInfo.InvariantCulture) + " " + Currency.Code;

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new DomainException("money.currency.mismatch");
        }
    }
}
