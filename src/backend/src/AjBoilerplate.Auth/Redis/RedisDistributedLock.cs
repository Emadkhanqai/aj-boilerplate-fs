using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Security;

namespace AjBoilerplate.Auth.Redis;

internal sealed class RedisDistributedLock : IDistributedLock
{
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;

    public RedisDistributedLock(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var redisKey = (RedisKey)RedisKeys.Lock(key);
        var token = SessionIds.NewRandom();
        var acquired = await _redis.GetDatabase().StringSetAsync(redisKey, token, lifetime, When.NotExists);
        return acquired ? new Handle(_redis, redisKey, token) : null;
    }

    private sealed class Handle(IConnectionMultiplexer redis, RedisKey key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [key], [token]);
    }
}
