using System.Globalization;
using Pishkhan.Infrastructure.Caching;
using StackExchange.Redis;

namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>Webhook results counted per hour (kept 49 hours) and the last time each result was seen, in the state Redis.</summary>
public sealed record WebhookStats(IReadOnlyDictionary<string, long> Last24Hours, IReadOnlyDictionary<string, DateTimeOffset> LastSeen);

internal static class InstagramWebhookStats
{
    private const string Area = "igwh-stats";

    private static string HourKey(RedisKeys keys, DateTimeOffset at) => keys.For(Area, at.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));

    public static void Record(IDatabase db, RedisKeys keys, DateTimeOffset now, string result)
    {
        var hour = HourKey(keys, now);
        db.HashIncrement(hour, result, 1, CommandFlags.FireAndForget);
        db.KeyExpire(hour, TimeSpan.FromHours(49), CommandFlags.FireAndForget);
        db.HashSet(keys.For(Area, "last"), result, now.ToUnixTimeSeconds(), flags: CommandFlags.FireAndForget);
    }

    public static async Task<WebhookStats> ReadAsync(IDatabase db, RedisKeys keys, DateTimeOffset now)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < 24; i++)
        {
            foreach (var entry in await db.HashGetAllAsync(HourKey(keys, now.AddHours(-i))))
            {
                totals[entry.Name!] = totals.GetValueOrDefault(entry.Name!) + (long)entry.Value;
            }
        }

        var last = (await db.HashGetAllAsync(keys.For(Area, "last")))
            .ToDictionary(e => e.Name.ToString(), e => DateTimeOffset.FromUnixTimeSeconds((long)e.Value), StringComparer.Ordinal);
        return new WebhookStats(totals, last);
    }
}
