using System.Text.Json;
using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Redis;

/// <summary>
/// Portal hand-off tickets (docs/auth/handoff.md): one string per ticket under
/// <c>auth:handoff:</c>, with the ticket lifetime as its TTL. Taken with a GET + DEL script, so
/// two redeems of one ticket cannot both succeed.
/// </summary>
internal sealed class RedisHandoffTicketStore : IHandoffTicketStore
{
    // Same shape as the login-state take: GETDEL needs Redis 6.2, a script is version-proof.
    private const string TakeScript = """
        local v = redis.call('GET', KEYS[1])
        if v then redis.call('DEL', KEYS[1]) end
        return v
        """;

    private readonly IConnectionMultiplexer _redis;

    public RedisHandoffTicketStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task SaveAsync(string ticketKey, HandoffTicket ticket, TimeSpan lifetime, CancellationToken cancellationToken) =>
        await _redis.GetDatabase().StringSetAsync(RedisKeys.Handoff(ticketKey), JsonSerializer.Serialize(ticket), lifetime);

    public async Task<HandoffTicket?> TakeAsync(string ticketKey, CancellationToken cancellationToken)
    {
        var value = await _redis.GetDatabase().ScriptEvaluateAsync(TakeScript, [RedisKeys.Handoff(ticketKey)]);
        if (value.IsNull)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HandoffTicket>((string)value!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
