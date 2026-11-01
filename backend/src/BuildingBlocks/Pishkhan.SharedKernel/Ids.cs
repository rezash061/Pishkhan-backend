using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Pishkhan.SharedKernel;

/// <summary>
/// Strongly typed IDs are <c>readonly record struct XxxId(Guid Value)</c> implementing this interface.
/// New values are UUID v7 (time ordered), which keeps B-tree inserts local.
/// </summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}

/// <summary>
/// UUID v7 that is monotonic within the process (RFC 9562 section 6.2, method 2): IDs created in the same millisecond
/// increment the random part instead of drawing new random bits, so ordering rows by id keeps creation order.
/// The outbox relay depends on this to publish a key's events in order. <see cref="Guid.CreateVersion7()"/> is not monotonic.
/// </summary>
public static class Uuid
{
    private const ulong RandBMask = (1UL << 62) - 1;

    /// <summary>Older timelines (backfill, test clocks) whose sequences are kept, so interleaving them keeps each in order.</summary>
    private const int MaxOlderTimelines = 64;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<long, (ushort RandA, ulong RandB)> Older = [];
    private static readonly Queue<long> OlderOrder = new();
    private static long _lastMs = -1;
    private static ushort _lastRandA;
    private static ulong _lastRandB;

    public static Guid NewV7() => NewV7(DateTimeOffset.UtcNow);

    public static Guid NewV7(DateTimeOffset at)
    {
        var ms = at.ToUnixTimeMilliseconds();
        ushort randA;
        ulong randB;
        lock (Gate)
        {
            if (ms <= _lastMs && ms >= _lastMs - 1000)
            {
                // The current timeline; small backward steps (clock adjustments) are pulled forward onto it.
                (ms, randA, randB) = Next(_lastMs, _lastRandA, _lastRandB);
                (_lastMs, _lastRandA, _lastRandB) = (ms, randA, randB);
            }
            else if (ms > _lastMs)
            {
                (randA, randB) = Fresh();
                (_lastMs, _lastRandA, _lastRandB) = (ms, randA, randB);
            }
            else
            {
                // A timestamp far behind (backfill, a fixed test clock): its own sequence per millisecond, leaving the current one alone.
                if (Older.TryGetValue(ms, out var last))
                {
                    long next;
                    (next, randA, randB) = Next(ms, last.RandA, last.RandB);
                    if (next != ms)
                    {
                        // Carried into the next millisecond: draw fresh bits there rather than tracking another entry.
                        (randA, randB) = Fresh();
                    }

                    ms = next;
                }
                else
                {
                    (randA, randB) = Fresh();
                    OlderOrder.Enqueue(ms);
                    if (OlderOrder.Count > MaxOlderTimelines)
                    {
                        Older.Remove(OlderOrder.Dequeue());
                    }
                }

                Older[ms] = (randA, randB);
            }
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, ((ulong)ms << 16) | 0x7000UL | randA);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (0b10UL << 62) | randB);
        return new Guid(bytes, bigEndian: true);
    }

    private static (long Ms, ushort RandA, ulong RandB) Next(long ms, ushort randA, ulong randB)
    {
        randB = (randB + 1) & RandBMask;
        if (randB == 0 && ++randA > 0xFFF)
        {
            randA = 0;
            ms++;
        }

        return (ms, randA, randB);
    }

    private static (ushort RandA, ulong RandB) Fresh()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        // Leave headroom so a burst within one millisecond rarely carries into rand_a.
        return ((ushort)(BinaryPrimitives.ReadUInt16BigEndian(random) & 0xFFF), BinaryPrimitives.ReadUInt64BigEndian(random[2..]) & (RandBMask >> 1));
    }
}
