using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class SessionValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly FakeLock _lock = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly SessionValidator _sut;
    private readonly string _sid = SessionIds.NewRandom();

    public SessionValidatorTests()
    {
        var options = TestConfig.Options();
        _sut = new SessionValidator(_store, _lock, _entra, _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());
    }

    private string Key => SessionIds.StorageKey(_sid);

    private AuthSession Seed(TimeSpan jwtLifetime, bool force = false, string? refreshToken = "rt-1")
    {
        var session = new AuthSession(
            Jwt.Create("kc-user", Now + jwtLifetime, TestConfig.ClientId, "requester"),
            Now + jwtLifetime,
            refreshToken,
            "kc-user",
            TestConfig.ClientId,
            force,
            Now - TimeSpan.FromMinutes(30));
        _store.Sessions[Key] = session;
        _store.Index.GetOrAdd("kc-user", _ => []).Add(Key);
        return session;
    }

    [Fact]
    public async Task Valid_session_is_served_lock_free_and_its_idle_ttl_slides()
    {
        var seeded = Seed(TimeSpan.FromMinutes(10));

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(seeded.KeycloakJwt, result.Jwt);
        Assert.Equal(0, _lock.Acquired);
        Assert.Equal(0, _entra.Refreshes);
        Assert.Equal(1, _store.Touches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-session-id")]
    public async Task Missing_or_malformed_cookie_is_401(string? sid)
    {
        var result = await _sut.ValidateAsync(sid, default);
        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
    }

    [Fact]
    public async Task Unknown_session_is_401()
    {
        var result = await _sut.ValidateAsync(_sid, default);
        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
    }

    [Fact]
    public async Task Near_expiry_refreshes_once_and_stores_new_jwt_and_rotated_refresh_token()
    {
        Seed(TimeSpan.FromSeconds(60)); // inside the 2-minute skew
        var fresh = Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), TestConfig.ClientId, "requester");
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, fresh);

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(fresh, result.Jwt);
        var stored = _store.Sessions[Key];
        Assert.Equal(fresh, stored.KeycloakJwt);
        Assert.Equal("entra-rt-2", stored.EntraRefreshToken);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), stored.JwtExpiresAt);
        Assert.Equal(1, _lock.Acquired);
    }

    [Fact]
    public async Task Entra_invalid_grant_ends_the_session_with_401()
    {
        Seed(TimeSpan.FromSeconds(60));
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.False(_store.Sessions.ContainsKey(Key));
        Assert.DoesNotContain(Key, _store.Index["kc-user"]);
    }

    [Fact]
    public async Task Entra_transient_keeps_the_session_and_serves_the_still_valid_token()
    {
        var seeded = Seed(TimeSpan.FromSeconds(60));
        _entra.NextRefresh = EntraTokenResult.Transient;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(seeded.KeycloakJwt, result.Jwt);
        Assert.True(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Transient_fault_after_expiry_is_503_never_401_and_keeps_the_session()
    {
        Seed(TimeSpan.FromSeconds(-10));
        _entra.NextRefresh = EntraTokenResult.Transient;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unavailable, result.Status);
        Assert.True(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Keycloak_transient_keeps_session_but_persists_the_rotated_refresh_token()
    {
        var seeded = Seed(TimeSpan.FromSeconds(60));
        _keycloak.Next = ExchangeResult.Transient;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(seeded.KeycloakJwt, result.Jwt);
        Assert.Equal("entra-rt-2", _store.Sessions[Key].EntraRefreshToken);
    }

    [Fact]
    public async Task Keycloak_denial_ends_the_session_with_403()
    {
        Seed(TimeSpan.FromSeconds(60));
        _keycloak.Next = ExchangeResult.Denied;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Forbidden, result.Status);
        Assert.False(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Token_without_a_role_for_this_portal_ends_the_session_with_403()
    {
        Seed(TimeSpan.FromSeconds(60));
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), "cad", "focal_point"));

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Forbidden, result.Status);
        Assert.False(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Force_refresh_flag_refreshes_even_a_fresh_token_and_clears_the_flag()
    {
        Seed(TimeSpan.FromMinutes(30), force: true);
        var fresh = Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), TestConfig.ClientId, "ba_manager");
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, fresh);

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(fresh, result.Jwt);
        Assert.False(_store.Sessions[Key].ForceRefresh);
        Assert.Equal(1, _entra.Refreshes);
    }

    [Fact]
    public async Task Session_without_refresh_token_cannot_renew_and_ends()
    {
        Seed(TimeSpan.FromSeconds(60), refreshToken: null);

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task Lock_loser_uses_the_winners_token_without_refreshing_itself()
    {
        Seed(TimeSpan.FromSeconds(60));
        _lock.HeldElsewhere = true;
        var winners = Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), TestConfig.ClientId, "requester");
        var reads = 0;
        _store.OnGet = key =>
        {
            if (++reads == 2)
            {
                _store.Sessions[key] = _store.Sessions[key] with { KeycloakJwt = winners, JwtExpiresAt = Now + TimeSpan.FromMinutes(5) };
            }
        };

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(winners, result.Jwt);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task Lock_loser_that_times_out_serves_a_still_valid_token()
    {
        var seeded = Seed(TimeSpan.FromSeconds(60));
        _lock.HeldElsewhere = true;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(seeded.KeycloakJwt, result.Jwt);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task Lock_loser_that_times_out_on_an_expired_token_reports_503_not_401()
    {
        Seed(TimeSpan.FromSeconds(-1));
        _lock.HeldElsewhere = true;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Lock_loser_sees_the_winner_end_the_session_and_returns_401()
    {
        Seed(TimeSpan.FromSeconds(60));
        _lock.HeldElsewhere = true;
        var reads = 0;
        _store.OnGet = key =>
        {
            if (++reads == 2)
            {
                _store.Sessions.TryRemove(key, out _);
            }
        };

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
    }

    [Fact]
    public async Task Lock_loser_waits_for_a_winner_slower_than_the_old_fixed_budget()
    {
        const int oldFixedBudgetPolls = 10;
        const int publishOnRead = oldFixedBudgetPolls + 4;

        Seed(TimeSpan.FromSeconds(-1));
        _lock.HeldElsewhere = true;

        var winners = Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), TestConfig.ClientId, "requester");
        var reads = 0;
        _store.OnGet = key =>
        {
            if (++reads == publishOnRead && _store.Sessions.TryGetValue(key, out var s))
            {
                _store.Sessions[key] = s with { KeycloakJwt = winners, JwtExpiresAt = Now + TimeSpan.FromMinutes(5) };
            }
        };

        var options = TestConfig.Options().Session;
        Assert.True(
            options.RefreshWaitAttempts > publishOnRead,
            $"the budget must outlast the winner for this to be a test: {options.RefreshWaitAttempts} polls vs publish on read {publishOnRead}");

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(winners, result.Jwt);

        Assert.Equal(0, _entra.Refreshes);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(5, 50)]
    [InlineData(5, 10)]
    [InlineData(30, 1000)]
    [InlineData(60, 10)]
    public void Losers_budget_always_outlasts_the_lock_it_waits_on(int lockSeconds, int pollMs)
    {
        var session = new AuthSessionOptions
        {
            RefreshLockLifetime = TimeSpan.FromSeconds(lockSeconds),
            LockPollInterval = TimeSpan.FromMilliseconds(pollMs),
        };

        Assert.True(session.RefreshWaitBudget > session.RefreshLockLifetime);

        Assert.True(session.RefreshWaitAttempts * session.LockPollInterval >= session.RefreshWaitBudget);
    }

    [Fact]
    public async Task Invalid_grant_from_a_refresh_that_outlived_its_lock_keeps_the_session()
    {
        var options = TestConfig.Options().Session;
        Seed(TimeSpan.FromSeconds(60));
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        _entra.OnRefresh = () => _time.Now += options.RefreshLockLifetime + TimeSpan.FromMilliseconds(1);

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.True(_store.Sessions.ContainsKey(Key));
        Assert.NotEqual(ValidationStatus.Unauthenticated, result.Status);
        Assert.Equal(ValidationStatus.Ok, result.Status);
    }

    [Fact]
    public void The_shipped_defaults_hold_a_user_for_5250ms_at_most_and_pass_validation()
    {
        var session = new AuthSessionOptions { EncryptionKey = TestConfig.Options().Session.EncryptionKey };

        Assert.Equal(TimeSpan.FromSeconds(5), session.RefreshLockLifetime);
        Assert.Equal(TimeSpan.FromMilliseconds(50), session.LockPollInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), session.LockExpiryGrace);

        Assert.Equal(TimeSpan.FromMilliseconds(5250), session.RefreshWaitBudget);
        Assert.Equal(105, session.RefreshWaitAttempts);

        Assert.True(session.RefreshWaitBudget < AuthSessionOptions.MaxRefreshWait);

        var options = TestConfig.Options();
        options.Session = session;
        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_lock_longer_than_the_ceiling_refuses_to_start_and_names_both_values()
    {
        var options = TestConfig.Options();
        options.Session = new AuthSessionOptions { RefreshLockLifetime = TimeSpan.FromSeconds(30) };

        var result = new AuthOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("RefreshLockLifetime", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("LockExpiryGrace", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_grant_within_the_lock_still_ends_the_session()
    {
        Seed(TimeSpan.FromSeconds(60));
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.False(_store.Sessions.ContainsKey(Key));
    }
}
