using System.Globalization;
using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Redis;

internal sealed class RedisSessionStore : ISessionStore
{
    private const string CreateScript = """
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], unpack(ARGV, 3))
        redis.call('PEXPIRE', KEYS[1], ARGV[1])
        redis.call('SADD', KEYS[2], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[1])
        return 1
        """;

    private const string UpdateTokensScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        redis.call('HSET', KEYS[1], 'jwt', ARGV[1], 'jwt_exp', ARGV[2], 'force_refresh', '0')
        if ARGV[3] ~= '' then redis.call('HSET', KEYS[1], 'rt', ARGV[3]) end
        return 1
        """;

    private const string SetIfExistsScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        return 1
        """;

    private const string TouchScript = """
        if redis.call('PEXPIRE', KEYS[1], ARGV[1]) == 0 then return 0 end
        redis.call('HSET', KEYS[1], 'last_seen', ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[1])
        return 1
        """;

    private const string StampIfMissingScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        local current = tonumber(redis.call('HGET', KEYS[1], ARGV[1]))
        if current and current > 0 then return 0 end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly SessionTokenProtector _protector;
    private readonly TimeProvider _time;

    public RedisSessionStore(IConnectionMultiplexer redis, SessionTokenProtector protector, TimeProvider time)
    {
        _redis = redis;
        _protector = protector;
        _time = time;
    }

    private IDatabase Db => _redis.GetDatabase();

    public async Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var entries = await Db.HashGetAllAsync(RedisKeys.Session(sessionKey));
        if (entries.Length == 0)
        {
            return null;
        }

        var map = entries.ToDictionary(e => (string)e.Name!, e => (string?)e.Value, StringComparer.Ordinal);
        string? Field(string name) => map.TryGetValue(name, out var v) ? v : null;

        var jwt = ReadToken(Field(SessionFields.Jwt));
        var sub = Field(SessionFields.Subject);
        var client = Field(SessionFields.ClientId);
        if (string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(client)
            || !long.TryParse(Field(SessionFields.JwtExp), NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
        {
            return null; // corrupt/partial hash: treat as no session
        }

        _ = long.TryParse(Field(SessionFields.CreatedAt), NumberStyles.None, CultureInfo.InvariantCulture, out var created);
        var rt = ReadToken(Field(SessionFields.RefreshToken));

        return new AuthSession(
            jwt,
            DateTimeOffset.FromUnixTimeSeconds(exp),
            string.IsNullOrEmpty(rt) ? null : rt,
            sub,
            client,
            Field(SessionFields.ForceRefresh) == "1",
            DateTimeOffset.FromUnixTimeSeconds(created),
            string.IsNullOrEmpty(Field(SessionFields.RefreshVia)) ? null : Field(SessionFields.RefreshVia));
    }

    private string? ReadToken(string? stored) =>
        string.IsNullOrEmpty(stored) ? stored : _protector.Unprotect(stored);

    private string Protect(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : _protector.Protect(value)!;

    public async Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        RedisValue[] args =
        [
            (long)idleTimeout.TotalMilliseconds,
            sessionKey,
            SessionFields.Jwt, Protect(session.KeycloakJwt),
            SessionFields.JwtExp, session.JwtExpiresAt.ToUnixTimeSeconds(),
            SessionFields.RefreshToken, Protect(session.EntraRefreshToken),
            SessionFields.Subject, session.Subject,
            SessionFields.ClientId, session.ClientId,
            SessionFields.ForceRefresh, session.ForceRefresh ? "1" : "0",
            SessionFields.CreatedAt, session.CreatedAt.ToUnixTimeSeconds(),
            SessionFields.LastSeen, _time.GetUtcNow().ToUnixTimeSeconds(),
            SessionFields.RefreshVia, session.RefreshVia ?? string.Empty,
        ];

        await Db.ScriptEvaluateAsync(
            CreateScript,
            [RedisKeys.Session(sessionKey), RedisKeys.SubjectIndex(session.Subject)],
            args);
    }

    public async Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken) =>
        await Db.ScriptEvaluateAsync(
            TouchScript,
            [RedisKeys.Session(sessionKey), RedisKeys.SubjectIndex(subject)],
            [(long)idleTimeout.TotalMilliseconds, _time.GetUtcNow().ToUnixTimeSeconds()]);

    public async Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken) =>
        await Db.ScriptEvaluateAsync(
            UpdateTokensScript,
            [RedisKeys.Session(sessionKey)],
            [Protect(jwt), jwtExpiresAt.ToUnixTimeSeconds(), Protect(refreshToken)]);

    public async Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken) =>
        await Db.ScriptEvaluateAsync(
            SetIfExistsScript,
            [RedisKeys.Session(sessionKey)],
            [SessionFields.RefreshToken, Protect(refreshToken)]);

    public async Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var result = await Db.ScriptEvaluateAsync(
            SetIfExistsScript,
            [RedisKeys.Session(sessionKey)],
            [SessionFields.ForceRefresh, "1"]);
        return (long)result == 1;
    }

    public async Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken) =>
        await Db.ScriptEvaluateAsync(
            StampIfMissingScript,
            [RedisKeys.Session(sessionKey)],
            [SessionFields.CreatedAt, signedInAt.ToUnixTimeSeconds()]);

    public async Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        var tx = Db.CreateTransaction();
        _ = tx.KeyDeleteAsync(RedisKeys.Session(sessionKey));
        _ = tx.SetRemoveAsync(RedisKeys.SubjectIndex(subject), sessionKey);
        await tx.ExecuteAsync();
    }

    public async Task<bool> EndAsync(string sessionKey, string subject, SessionEnd ending, CancellationToken cancellationToken)
    {
        var tx = Db.CreateTransaction();
        var deleted = tx.KeyDeleteAsync(RedisKeys.Session(sessionKey));
        _ = tx.SetRemoveAsync(RedisKeys.SubjectIndex(subject), sessionKey);
        await tx.ExecuteAsync();
        return await deleted;
    }

    public async Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var members = await Db.SetMembersAsync(RedisKeys.SubjectIndex(subject));
        return [.. members.Where(m => m.HasValue).Select(m => (string)m!)];
    }

    public async Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken) =>
        await Db.SetRemoveAsync(RedisKeys.SubjectIndex(subject), sessionKey);
}
