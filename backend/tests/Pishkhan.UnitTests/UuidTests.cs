using Pishkhan.SharedKernel;
using Shouldly;
using Xunit;

namespace Pishkhan.UnitTests;

public sealed class UuidTests
{
    [Fact]
    public void Ids_created_in_the_same_millisecond_sort_in_creation_order()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(5);
        var ids = Enumerable.Range(0, 10_000).Select(_ => Uuid.NewV7(at)).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.Select(g => g.ToString()).ShouldBe(ids.Select(g => g.ToString()).Order(StringComparer.Ordinal));
        ids.ShouldAllBe(g => g.Version == 7 && g.Variant >= 8 && g.Variant <= 0xB);
    }

    [Fact]
    public void The_timestamp_prefix_is_the_creation_time()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(10);
        var id = Uuid.NewV7(at);

        Convert.ToInt64(id.ToString("N")[..12], 16).ShouldBe(at.ToUnixTimeMilliseconds());
    }
}
