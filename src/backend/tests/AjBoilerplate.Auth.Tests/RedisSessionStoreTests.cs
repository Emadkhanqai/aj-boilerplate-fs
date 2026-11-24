using StackExchange.Redis;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using AjBoilerplate.Auth.Redis;

namespace AjBoilerplate.Auth.Tests;

public sealed class RedisFactAttribute : FactAttribute
{
    public const string Variable = "AUTH_TEST_REDIS";

    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"{Variable} is not set; these tests need a real Redis.";
        }
    }
}

public sealed class RedisSessionStoreTests : IAsyncLifetime
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0LXN1YmplY3QifQ.c2lnbmF0dXJlLXBsYWNlaG9sZGVy";
    private const string RefreshToken = "entra-refresh-token-plaintext-marker";

    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly string _sessionKey = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _subject = "sub-" + Guid.NewGuid().ToString("N");
    private readonly SessionTokenProtector _protector = new(SessionTokenProtector.GenerateKey());
    private ConnectionMultiplexer? _redis;

    private IDatabase Db => _redis!.GetDatabase();

    public async Task InitializeAsync()
    {
        var target = Environment.GetEnvironmentVariable(RedisFactAttribute.Variable);
        if (!string.IsNullOrWhiteSpace(target))
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(target);
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await Db.KeyDeleteAsync(RedisKeys.Session(_sessionKey));
            await Db.KeyDeleteAsync(RedisKeys.SubjectIndex(_subject));
            await _redis.DisposeAsync();
        }
    }

    private RedisSessionStore Store() => new(_redis!, _protector, _time);

    private AuthSession Session() =>
        new(Jwt, _time.Now.AddMinutes(5), RefreshToken, _subject, "my-app", false, _time.Now);

    private async Task<string> RawFieldsAsync() =>
        string.Join("\n", (await Db.HashGetAllAsync(RedisKeys.Session(_sessionKey))).Select(e => $"{e.Name}={e.Value}"));

    [RedisFact]
    public async Task Create_stores_neither_the_jwt_nor_the_refresh_token_in_the_clear()
    {
        await Store().CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);

        var raw = await RawFieldsAsync();
        Assert.Contains("sub=" + _subject, raw, StringComparison.Ordinal); // positive control: the hash IS there
        Assert.DoesNotContain(Jwt, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, raw, StringComparison.Ordinal);
    }

    [RedisFact]
    public async Task A_created_session_reads_back_with_its_plaintext_tokens()
    {
        await Store().CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);

        var read = await Store().GetAsync(_sessionKey, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(Jwt, read.KeycloakJwt);
        Assert.Equal(RefreshToken, read.EntraRefreshToken);
        Assert.Equal(_subject, read.Subject);
    }

    [RedisFact]
    public async Task A_plaintext_session_is_not_trusted_and_reads_as_no_session()
    {
        var key = RedisKeys.Session(_sessionKey);
        await Db.HashSetAsync(key,
        [
            new(SessionFields.Jwt, Jwt), new(SessionFields.JwtExp, _time.Now.AddMinutes(5).ToUnixTimeSeconds()), new(SessionFields.RefreshToken, RefreshToken),
            new(SessionFields.Subject, _subject), new(SessionFields.ClientId, "my-app"),
            new(SessionFields.ForceRefresh, "0"), new(SessionFields.CreatedAt, _time.Now.ToUnixTimeSeconds()), new(SessionFields.LastSeen, _time.Now.ToUnixTimeSeconds()),
        ]);
        Assert.Contains(Jwt, await RawFieldsAsync(), StringComparison.Ordinal); // control: the plant IS plaintext

        Assert.Null(await Store().GetAsync(_sessionKey, CancellationToken.None));
    }

    [RedisFact]
    public async Task A_refresh_stores_the_new_tokens_encrypted()
    {
        const string newJwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJyZWZyZXNoZWQifQ.bmV3LXNpZ25hdHVyZQ";
        const string newRt = "rotated-refresh-token-marker";
        await Store().CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);

        await Store().UpdateTokensAsync(_sessionKey, newJwt, _time.Now.AddMinutes(10), newRt, CancellationToken.None);
        var raw = await RawFieldsAsync();
        Assert.DoesNotContain(newJwt, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(newRt, raw, StringComparison.Ordinal);

        const string onlyRt = "second-rotation-marker";
        await Store().UpdateRefreshTokenAsync(_sessionKey, onlyRt, CancellationToken.None);
        Assert.DoesNotContain(onlyRt, await RawFieldsAsync(), StringComparison.Ordinal);

        var read = await Store().GetAsync(_sessionKey, CancellationToken.None);
        Assert.Equal(newJwt, read!.KeycloakJwt);
        Assert.Equal(onlyRt, read.EntraRefreshToken);
    }

    [RedisFact]
    public async Task A_refresh_that_keeps_the_refresh_token_does_not_blank_it()
    {
        await Store().CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);
        await Store().UpdateTokensAsync(_sessionKey, Jwt, _time.Now.AddMinutes(10), refreshToken: null, CancellationToken.None);
        Assert.Equal(RefreshToken, (await Store().GetAsync(_sessionKey, CancellationToken.None))!.EntraRefreshToken);
    }

    [RedisFact]
    public async Task A_session_encrypted_under_another_key_reads_as_absent()
    {
        var other = new RedisSessionStore(_redis!, new SessionTokenProtector(SessionTokenProtector.GenerateKey()), _time);
        await other.CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.Null(await Store().GetAsync(_sessionKey, CancellationToken.None));
    }

    [RedisFact]
    public async Task Stamping_sets_a_missing_sign_in_time_and_never_moves_a_recorded_one()
    {
        await Store().CreateAsync(_sessionKey, Session(), TimeSpan.FromMinutes(10), CancellationToken.None);
        await Db.HashDeleteAsync(RedisKeys.Session(_sessionKey), SessionFields.CreatedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, (await Store().GetAsync(_sessionKey, CancellationToken.None))!.CreatedAt);

        var firstSeen = _time.Now.AddMinutes(3);
        await Store().StampSignInTimeAsync(_sessionKey, firstSeen, CancellationToken.None);
        Assert.Equal(firstSeen, (await Store().GetAsync(_sessionKey, CancellationToken.None))!.CreatedAt);

        await Store().StampSignInTimeAsync(_sessionKey, firstSeen.AddHours(5), CancellationToken.None);
        Assert.Equal(firstSeen, (await Store().GetAsync(_sessionKey, CancellationToken.None))!.CreatedAt);
    }

    [RedisFact]
    public async Task Stamping_a_session_that_no_longer_exists_does_not_recreate_it()
    {
        await Store().StampSignInTimeAsync(_sessionKey, _time.Now, CancellationToken.None);
        Assert.False(await Db.KeyExistsAsync(RedisKeys.Session(_sessionKey)));
    }
}

public sealed class RedisRevokedSessionMarksTests : IAsyncLifetime
{
    private readonly string _sessionKey = "test-" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer? _redis;

    public async Task InitializeAsync()
    {
        var target = Environment.GetEnvironmentVariable(RedisFactAttribute.Variable);
        if (!string.IsNullOrWhiteSpace(target))
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(target);
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.GetDatabase().KeyDeleteAsync(RedisKeys.Revoked(_sessionKey));
            await _redis.DisposeAsync();
        }
    }

    private RedisRevokedSessionMarks Marks() => new(_redis!, Log.For<RedisRevokedSessionMarks>());

    [RedisFact]
    public async Task A_mark_is_read_back_under_the_prefix_with_its_lifetime()
    {
        Assert.False(await Marks().IsMarkedAsync(_sessionKey, CancellationToken.None)); // control

        await Marks().MarkAsync(_sessionKey, TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.True(await Marks().IsMarkedAsync(_sessionKey, CancellationToken.None));
        var ttl = await _redis!.GetDatabase().KeyTimeToLiveAsync("auth:revoked:" + _sessionKey);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [RedisFact]
    public async Task A_redis_that_is_gone_reads_as_not_marked_and_a_mark_does_not_throw()
    {
        await Marks().MarkAsync(_sessionKey, TimeSpan.FromMinutes(15), CancellationToken.None);
        var marks = Marks();
        await _redis!.CloseAsync();

        Assert.False(await marks.IsMarkedAsync(_sessionKey, CancellationToken.None));
        await marks.MarkAsync(_sessionKey, TimeSpan.FromMinutes(15), CancellationToken.None);

        _redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable(RedisFactAttribute.Variable)!);
    }
}
