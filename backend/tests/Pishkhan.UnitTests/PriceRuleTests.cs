using Pishkhan.Modules.Catalog.Domain;

namespace Pishkhan.UnitTests;

public class PriceRuleTests
{
    [Theory]
    // 1,234,000 Toman + 10% = 1,357,400 → nearest 1,000 = 1,357,000; up = 1,358,000; down = 1,357,000.
    [InlineData(12_340_000, "percent", 10, 1_000, "nearest", 13_570_000L)]
    [InlineData(12_340_000, "percent", 10, 1_000, "up", 13_580_000L)]
    [InlineData(12_340_000, "percent", 10, 1_000, "down", 13_570_000L)]
    // Half way rounds away from zero: 1,500 Toman → 2,000.
    [InlineData(15_000, "percent", 0, 1_000, "nearest", 20_000L)]
    [InlineData(2_500_000, "percent", -15, 1, "nearest", 2_125_000L)]
    // 99,999 Rial is 9,999.9 Toman: whole-Toman rounding gives 10,000 Toman.
    [InlineData(99_999, "percent", 0, 1, "nearest", 100_000L)]
    [InlineData(1_000_000, "amount", 50_000, 1, "nearest", 1_050_000L)]
    [InlineData(1_000_000, "amount", -1_000_000, 1, "nearest", null)]
    [InlineData(10_000, "percent", -90, 1_000, "down", null)]
    public void Applies_and_rounds_in_toman(long rial, string kind, double value, long roundTo, string mode, long? expected) =>
        new PriceRule(kind, (decimal)value, roundTo, mode).Apply(rial).ShouldBe(expected);
}
