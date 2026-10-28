using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.SharedKernel;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.RateLimiting;

public readonly record struct RateLimitDecision(bool Allowed, long Remaining, TimeSpan RetryAfter);

/// <summary>Token bucket in Redis (Lua, atomic). Used to pace Instagram API calls per account (backend plan 3.5).</summary>
public sealed class RedisTokenBucket([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, IClock clock)
{
    // KEYS[1] bucket; ARGV: capacity, refill tokens per second, now (ms), requested tokens.
    private const string Script = """
        local capacity = tonumber(ARGV[1])
        local rate = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local requested = tonumber(ARGV[4])
        local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(data[1])
        local ts = tonumber(data[2])
        if tokens == nil then
          tokens = capacity
          ts = now
        end
        local elapsed = math.max(0, now - ts)
        tokens = math.min(capacity, tokens + (elapsed * rate / 1000))
        local allowed = 0
        local retry = 0
        if tokens >= requested then
          tokens = tokens - requested
          allowed = 1
        else
          retry = math.ceil((requested - tokens) * 1000 / rate)
        end
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', now)
        redis.call('PEXPIRE', KEYS[1], math.ceil(capacity * 1000 / rate) + 1000)
        return { allowed, math.floor(tokens), retry }
        """;

    public async Task<RateLimitDecision> TryTakeAsync(string bucket, int capacity, double refillPerSecond, int tokens = 1)
    {
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [keys.For("rl", bucket)],
            [capacity, refillPerSecond, clock.UtcNow.ToUnixTimeMilliseconds(), tokens]))!;
        return new RateLimitDecision((int)result[0] == 1, (long)result[1], TimeSpan.FromMilliseconds((long)result[2]));
    }
}

/// <summary>Sliding window log in Redis (sorted set). Used for API and auth limits (per phone, per IP, per user).</summary>
public sealed class RedisSlidingWindow([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, IClock clock)
{
    // KEYS[1] window; ARGV: now (ms), window (ms), limit, member.
    private const string Script = """
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)
        local count = redis.call('ZCARD', KEYS[1])
        if count < limit then
          redis.call('ZADD', KEYS[1], now, ARGV[4])
          redis.call('PEXPIRE', KEYS[1], window)
          return { 1, limit - count - 1, 0 }
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        local retry = window
        if oldest[2] ~= nil then retry = tonumber(oldest[2]) + window - now end
        return { 0, 0, retry }
        """;

    public async Task<RateLimitDecision> TryHitAsync(string window, int limit, TimeSpan period)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [keys.For("rl", window)],
            [now, (long)period.TotalMilliseconds, limit, $"{now}-{Guid.NewGuid():N}"]))!;
        return new RateLimitDecision((int)result[0] == 1, (long)result[1], TimeSpan.FromMilliseconds(Math.Max(0, (long)result[2])));
    }
}
