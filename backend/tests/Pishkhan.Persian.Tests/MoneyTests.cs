using Pishkhan.SharedKernel;

namespace Pishkhan.Persian.Tests;

public class MoneyTests
{
    [Fact]
    public void Toman_is_ten_rial()
    {
        Money.Toman(125_000).Amount.ShouldBe(1_250_000);
        Money.Rial(1_250_000).ToToman().ShouldBe(125_000);
    }

    [Fact]
    public void Arithmetic_keeps_currency()
    {
        (Money.Rial(100) + Money.Rial(50)).ShouldBe(Money.Rial(150));
        (Money.Rial(100) - Money.Rial(150)).IsNegative.ShouldBeTrue();
        (Money.Rial(100) * 3).ShouldBe(Money.Rial(300));
    }

    [Theory]
    [InlineData(1_234_567L, 1000L, 1_230_000L)]
    [InlineData(1_235_000L, 1000L, 1_240_000L)]
    [InlineData(99_990L, 1000L, 100_000L)]
    [InlineData(4_999L, 1000L, 0L)]
    public void Round_to_toman_step(long rial, long step, long expected) => Money.Rial(rial).RoundToToman(step).Amount.ShouldBe(expected);

    [Fact]
    public void Overflow_is_detected() => Should.Throw<OverflowException>(() => Money.Rial(long.MaxValue) + Money.Rial(1));
}
