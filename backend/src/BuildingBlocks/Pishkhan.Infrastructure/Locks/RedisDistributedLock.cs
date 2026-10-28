using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Pishkhan.Infrastructure.Caching;

namespace Pishkhan.Infrastructure.Locks;

public interface IDistributedLock
{
    /// <summary>Tries to take the lock, waiting up to <paramref name="wait"/>. Returns null if another holder has it.</summary>
    Task<ILockHandle?> TryAcquireAsync(string resource, TimeSpan? ttl = null, TimeSpan? wait = null, CancellationToken cancellationToken = default);
}

public interface ILockHandle : IAsyncDisposable
{
    string Resource { get; }

    /// <summary>False once renewal failed: the lock may now be held by someone else.</summary>
    bool IsHeld { get; }
}

/// <summary>
/// Redis lock on the <c>state</c> instance: <c>SET NX PX</c> with a random token, renewed in the background every ttl/3,
/// released with a Lua compare-and-delete so a holder never deletes someone else's lock.
/// </summary>
public sealed class RedisDistributedLock([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys) : IDistributedLock
{
    private static readonly LuaScript Release = LuaScript.Prepare("if redis.call('get', @key) == @token then return redis.call('del', @key) else return 0 end");
    private static readonly LuaScript Renew = LuaScript.Prepare("if redis.call('get', @key) == @token then return redis.call('pexpire', @key, @ttl) else return 0 end");

    public async Task<ILockHandle?> TryAcquireAsync(string resource, TimeSpan? ttl = null, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var lifetime = ttl ?? TimeSpan.FromSeconds(30);
        var key = keys.For("lock", resource);
        var token = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();
        var deadline = DateTimeOffset.UtcNow + (wait ?? TimeSpan.Zero);

        while (true)
        {
            if (await db.StringSetAsync(key, token, lifetime, When.NotExists))
            {
                return new Handle(db, resource, key, token, lifetime);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(25, 75)), cancellationToken);
        }
    }

    private sealed class Handle : ILockHandle
    {
        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _token;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;

        public Handle(IDatabase db, string resource, string key, string token, TimeSpan ttl)
        {
            _db = db;
            _key = key;
            _token = token;
            Resource = resource;
            _renewal = RenewLoopAsync(ttl);
        }

        public string Resource { get; }

        public bool IsHeld { get; private set; } = true;

        private async Task RenewLoopAsync(TimeSpan ttl)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(ttl / 3, _stop.Token);
                    var renewed = (int)await _db.ScriptEvaluateAsync(Renew, new { key = (RedisKey)_key, token = _token, ttl = (long)ttl.TotalMilliseconds });
                    if (renewed == 0)
                    {
                        IsHeld = false;
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // released
            }
            catch (RedisException)
            {
                IsHeld = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _renewal;
            _stop.Dispose();
            if (IsHeld)
            {
                await _db.ScriptEvaluateAsync(Release, new { key = (RedisKey)_key, token = _token });
                IsHeld = false;
            }
        }
    }
}
