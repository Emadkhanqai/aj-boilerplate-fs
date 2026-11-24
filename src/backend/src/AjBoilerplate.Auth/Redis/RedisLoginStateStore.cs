using System.Text.Json;
using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Redis;

internal sealed class RedisLoginStateStore : ILoginStateStore
{
    private const string TakeScript = """
        local v = redis.call('GET', KEYS[1])
        if v then redis.call('DEL', KEYS[1]) end
        return v
        """;

    private readonly IConnectionMultiplexer _redis;

    public RedisLoginStateStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task SaveAsync(string state, LoginState login, TimeSpan lifetime, CancellationToken cancellationToken) =>
        await _redis.GetDatabase().StringSetAsync(RedisKeys.Login(state), JsonSerializer.Serialize(login), lifetime);

    public async Task<LoginState?> TakeAsync(string state, CancellationToken cancellationToken)
    {
        var value = await _redis.GetDatabase().ScriptEvaluateAsync(TakeScript, [RedisKeys.Login(state)]);
        if (value.IsNull)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoginState>((string)value!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
