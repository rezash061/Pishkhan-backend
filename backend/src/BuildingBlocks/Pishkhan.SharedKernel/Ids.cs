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
    private static readonly Lock Gate = new();
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
            // A timestamp far behind the last one (backfill, a fixed test clock) starts a new sequence instead of
            // being pulled forward; small backward steps (clock adjustments) stay on the current sequence.
            if (ms <= _lastMs && ms >= _lastMs - 1000)
            {
                ms = _lastMs;
                randA = _lastRandA;
                randB = (_lastRandB + 1) & RandBMask;
                if (randB == 0 && ++randA > 0xFFF)
                {
                    randA = 0;
                    ms++;
                }
            }
            else
            {
                Span<byte> random = stackalloc byte[10];
                RandomNumberGenerator.Fill(random);
                randA = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(random) & 0xFFF);
                // Leave headroom so a burst within one millisecond rarely carries into rand_a.
                randB = BinaryPrimitives.ReadUInt64BigEndian(random[2..]) & (RandBMask >> 1);
            }

            _lastMs = ms;
            _lastRandA = randA;
            _lastRandB = randB;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, ((ulong)ms << 16) | 0x7000UL | randA);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (0b10UL << 62) | randB);
        return new Guid(bytes, bigEndian: true);
    }
}
