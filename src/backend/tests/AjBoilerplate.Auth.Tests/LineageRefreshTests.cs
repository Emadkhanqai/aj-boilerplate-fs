using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// <c>/auth/validate</c> for a handed-off session (docs/auth/handoff.md): it renews from
/// its lineage root's refresh token, under the ROOT's lock, and writes the rotation back to the root.
/// </summary>
public sealed class LineageRefreshTests
{
    private const string OtherHost = TestConfig.OtherHost;
    private const string Subject = "kc-user";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly FakeLock _lock = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly SessionValidator _sut;

    private readonly string _rootKey = SessionIds.StorageKey(SessionIds.NewRandom());
    private readonly string _derivedId = SessionIds.NewRandom();

    public LineageRefreshTests()
    {
        var options = TestConfig.TwoPortals();
        _sut = new SessionValidator(_store, _lock, _entra, _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, Now.AddMinutes(30), TestConfig.OtherClientId, "requester"));

        _store.Sessions[_rootKey] = new AuthSession("root-jwt", Now.AddMinutes(30), "rt-root", Subject, TestConfig.ClientId, false, Now.AddHours(-1));

        // Due for refresh: inside the 2-minute skew, but still servable.
        _store.Sessions[DerivedKey] = new AuthSession("derived-jwt", Now.AddMinutes(1), null, Subject, TestConfig.OtherClientId, false, Now.AddHours(-1), _rootKey);
    }

    private string DerivedKey => SessionIds.StorageKey(_derivedId);

    private Task<ValidationResult> ValidateAsync() => _sut.ValidateAsync(_derivedId, OtherHost, default);

    [Fact]
    public async Task A_derived_session_renews_from_the_roots_token_under_the_roots_lock()
    {
        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal("rt-root", _entra.LastRefreshToken);
        Assert.Equal([SessionValidator.LockKey(_rootKey)], _lock.Keys);

        // The rotation lands on the OWNER of the grant; the derived session still holds none.
        Assert.Equal("entra-rt-2", _store.Sessions[_rootKey].EntraRefreshToken);
        Assert.Null(_store.Sessions[DerivedKey].EntraRefreshToken);
        Assert.NotEqual("derived-jwt", _store.Sessions[DerivedKey].KeycloakJwt);

        // Own touch plus the root's: activity on the derived session keeps the grant's owner alive.
        Assert.Equal(2, _store.Touches);
    }

    [Fact]
    public async Task The_rotated_token_reaches_the_root_even_when_keycloak_then_fails()
    {
        _keycloak.Next = ExchangeResult.Transient;

        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal("entra-rt-2", _store.Sessions[_rootKey].EntraRefreshToken);
    }

    [Fact]
    public async Task A_derived_session_whose_root_is_gone_ends()
    {
        _store.Sessions.TryRemove(_rootKey, out _);

        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.False(_store.Sessions.ContainsKey(DerivedKey));
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task An_uncontended_invalid_grant_ends_the_derived_session_and_leaves_the_root()
    {
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.False(_store.Sessions.ContainsKey(DerivedKey));
        Assert.True(_store.Sessions.ContainsKey(_rootKey));
    }

    [Fact]
    public async Task A_waiter_that_acquires_the_lock_mid_wait_refreshes_itself()
    {
        // A waiter that only polled its own session would wait out the whole budget every time when the winner is ANOTHER session of
        // the lineage, because this session never gets fresher.
        _lock.FailNext = 2;

        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal(1, _entra.Refreshes);
        Assert.Equal(3, _lock.Keys.Count);
    }

    [Fact]
    public async Task An_invalid_grant_on_a_lock_got_by_waiting_keeps_the_session()
    {
        // The previous holder may still be in flight with the same rotating token, so the verdict
        // cannot be attributed to the user.
        _lock.FailNext = 1;
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        var result = await ValidateAsync();

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.True(_store.Sessions.ContainsKey(DerivedKey));
    }

    [Fact]
    public async Task A_root_session_waiting_on_its_lock_also_acquires_it_mid_wait()
    {
        // The root's own refresh competes for the same lock as its derived sessions.
        var rootId = SessionIds.NewRandom();
        var rootKey = SessionIds.StorageKey(rootId);
        _store.Sessions[rootKey] = new AuthSession("root-jwt", Now.AddMinutes(1), "rt-own", Subject, TestConfig.ClientId, false, Now.AddHours(-1));
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, Now.AddMinutes(30), TestConfig.ClientId, "requester"));
        _lock.FailNext = 1;

        var result = await _sut.ValidateAsync(rootId, TestConfig.Host, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Equal("rt-own", _entra.LastRefreshToken);
        Assert.Equal("entra-rt-2", _store.Sessions[rootKey].EntraRefreshToken);
    }
}
