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
    public void Interleaved_clocks_each_keep_their_order()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(20);
        var current = new List<Guid>();
        var older = new List<Guid>();
        for (var i = 0; i < 1_000; i++)
        {
            current.Add(Uuid.NewV7(at));
            older.Add(Uuid.NewV7(at.AddMinutes(-10))); // another caller with an older clock
            Uuid.NewV7(at.AddMinutes(-20));
        }

        current.Select(g => g.ToString()).ShouldBe(current.Select(g => g.ToString()).Order(StringComparer.Ordinal));
        older.Select(g => g.ToString()).ShouldBe(older.Select(g => g.ToString()).Order(StringComparer.Ordinal));
        current.Concat(older).Distinct().Count().ShouldBe(2_000);
    }

    [Fact]
    public void The_timestamp_prefix_is_the_creation_time()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(10);
        var id = Uuid.NewV7(at);

        Convert.ToInt64(id.ToString("N")[..12], 16).ShouldBe(at.ToUnixTimeMilliseconds());
    }
}
