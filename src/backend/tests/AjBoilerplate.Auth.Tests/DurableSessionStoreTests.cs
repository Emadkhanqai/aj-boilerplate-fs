using Microsoft.Extensions.Logging.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class DurableSessionStoreTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(8);

    private readonly InMemorySessionStore _database = new();
    private readonly InMemorySessionStore _cache = new();
    private readonly DurableSessionStore _store;

    public DurableSessionStoreTests() =>
        _store = new DurableSessionStore(_database, _cache, NullLogger<DurableSessionStore>.Instance)
        {
            IdleTimeout = Idle,
        };

    private static AuthSession Session(string subject = "user-1") => new(
        KeycloakJwt: "jwt-1",
        JwtExpiresAt: DateTimeOffset.UnixEpoch.AddYears(60),
        EntraRefreshToken: "rt-1",
        Subject: subject,
        ClientId: "my-app",
        ForceRefresh: false,
        CreatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Create_writes_to_both()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);

        Assert.True(_database.Sessions.ContainsKey("k1"));
        Assert.True(_cache.Sessions.ContainsKey("k1"));
    }

    [Fact]
    public async Task Read_is_served_from_the_cache_without_touching_the_database()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);

        var reads = 0;
        _database.OnGet = _ => reads++;

        Assert.NotNull(await _store.GetAsync("k1", CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task A_flushed_cache_costs_a_database_read_not_a_sign_out()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);
        _cache.Sessions.Clear();   // Redis restarted

        var session = await _store.GetAsync("k1", CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("jwt-1", session.KeycloakJwt);
        Assert.True(_cache.Sessions.ContainsKey("k1"));   // and it is warm again
    }

    [Fact]
    public async Task Unknown_key_is_no_session_in_either_store()
    {
        Assert.Null(await _store.GetAsync("nope", CancellationToken.None));
        Assert.Empty(_cache.Sessions);
    }

    [Fact]
    public async Task Sign_out_removes_the_cached_copy_too()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);

        await _store.DeleteAsync("k1", "user-1", CancellationToken.None);

        Assert.False(_database.Sessions.ContainsKey("k1"));
        Assert.False(_cache.Sessions.ContainsKey("k1"));
        Assert.Null(await _store.GetAsync("k1", CancellationToken.None));
    }

    [Fact]
    public async Task Force_refresh_reaches_the_cache_even_when_the_row_is_already_gone()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);
        _database.Sessions.TryRemove("k1", out _);

        var existed = await _store.MarkForceRefreshAsync("k1", CancellationToken.None);

        Assert.False(existed);
        Assert.True(_cache.Sessions["k1"].ForceRefresh);
    }

    [Fact]
    public async Task Token_update_lands_in_both()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);
        var expiry = DateTimeOffset.UnixEpoch.AddYears(61);

        await _store.UpdateTokensAsync("k1", "jwt-2", expiry, "rt-2", CancellationToken.None);

        Assert.Equal("jwt-2", _database.Sessions["k1"].KeycloakJwt);
        Assert.Equal("jwt-2", _cache.Sessions["k1"].KeycloakJwt);
        Assert.Equal(expiry, _cache.Sessions["k1"].JwtExpiresAt);
    }

    [Fact]
    public async Task Role_change_fan_out_asks_the_database_not_the_cache()
    {
        await _store.CreateAsync("k1", Session(), Idle, CancellationToken.None);
        await _store.CreateAsync("k2", Session(), Idle, CancellationToken.None);
        _cache.Index.Clear();   // an evicted index must not shrink the fan-out

        var keys = await _store.GetSessionKeysForSubjectAsync("user-1", CancellationToken.None);

        Assert.Equal(2, keys.Count);
    }
}
