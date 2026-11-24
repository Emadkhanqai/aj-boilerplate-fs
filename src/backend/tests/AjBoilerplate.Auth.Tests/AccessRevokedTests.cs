using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class AccessRevokedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly InMemoryRevokedSessionMarks _marks = new();
    private readonly FakeLock _lock = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly AuthOptions _options = TestConfig.Options();
    private readonly SessionValidator _sut;
    private readonly string _sid = SessionIds.NewRandom();

    public AccessRevokedTests()
    {
        _sut = new SessionValidator(_store, _lock, _entra, _keycloak, TestConfig.Wrap(_options), _time, Log.For<SessionValidator>(), _marks);
    }

    private string Key => SessionIds.StorageKey(_sid);

    private void SeedRefreshing(TimeSpan jwtLeft)
    {
        _store.Sessions[Key] = new AuthSession(
            Jwt.Create("kc-user", Now + jwtLeft, TestConfig.ClientId, "requester"),
            Now + jwtLeft,
            "rt-1",
            "kc-user",
            TestConfig.ClientId,
            ForceRefresh: false,
            Now.AddHours(-1));
        _store.Index.GetOrAdd("kc-user", _ => []).Add(Key);
    }

    private Task<ValidationResult> ValidateAsync() => _sut.ValidateAsync(_sid, default);

    [Fact]
    public async Task A_keycloak_denial_is_forbidden_and_leaves_a_revoked_mark()
    {
        SeedRefreshing(TimeSpan.FromSeconds(60));
        _keycloak.Next = ExchangeResult.Denied;

        Assert.Equal(ValidationStatus.Forbidden, (await ValidateAsync()).Status);
        Assert.Equal(_options.Session.RevokedMarkerLifetime, _marks.Marks[Key]);
        Assert.False(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task A_token_with_no_client_role_is_forbidden_and_leaves_a_revoked_mark()
    {
        SeedRefreshing(TimeSpan.FromSeconds(60));
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("kc-user", Now.AddMinutes(30), TestConfig.ClientId));

        Assert.Equal(ValidationStatus.Forbidden, (await ValidateAsync()).Status);
        Assert.True(_marks.Marks.ContainsKey(Key));
    }

    [Theory]
    [InlineData("entra", 60)]
    [InlineData("keycloak", 60)]
    [InlineData("unreadable", 60)]
    [InlineData("entra", -10)]
    [InlineData("keycloak", -10)]
    [InlineData("unreadable", -10)]
    public async Task An_infrastructure_fault_is_never_forbidden_and_leaves_no_mark(string fault, int jwtSecondsLeft)
    {
        SeedRefreshing(TimeSpan.FromSeconds(jwtSecondsLeft));
        switch (fault)
        {
            case "entra":
                _entra.NextRefresh = EntraTokenResult.Transient;
                break;
            case "keycloak":
                _keycloak.Next = ExchangeResult.Transient;
                break;
            default:
                _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, "not-a-jwt");
                break;
        }

        var result = await ValidateAsync();

        Assert.Equal(jwtSecondsLeft > 0 ? ValidationStatus.Ok : ValidationStatus.Unavailable, result.Status);
        Assert.Empty(_marks.Marks);
        Assert.True(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Entra_invalid_grant_ends_the_session_as_401_not_as_revoked()
    {
        SeedRefreshing(TimeSpan.FromSeconds(60));
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        Assert.Equal(ValidationStatus.Unauthenticated, (await ValidateAsync()).Status);
        Assert.Empty(_marks.Marks);
    }

    [Fact]
    public async Task A_cookie_for_a_session_already_ended_as_revoked_is_still_forbidden()
    {
        _marks.Marks[Key] = TimeSpan.FromMinutes(15);

        Assert.Equal(ValidationStatus.Forbidden, (await ValidateAsync()).Status);
    }

    [Fact]
    public async Task A_cookie_for_a_session_that_is_simply_gone_is_401()
    {
        Assert.Equal(ValidationStatus.Unauthenticated, (await ValidateAsync()).Status);
    }

    [Fact]
    public async Task A_marks_store_that_is_down_answers_401_for_a_gone_session_never_403()
    {
        _marks.Marks[Key] = TimeSpan.FromMinutes(15);
        _marks.Fault = new TimeoutException("store down");

        Assert.Equal(ValidationStatus.Unauthenticated, (await ValidateAsync()).Status);
    }

    [Fact]
    public async Task A_marks_store_that_is_down_does_not_stop_a_denial_ending_the_session()
    {
        SeedRefreshing(TimeSpan.FromSeconds(60));
        _keycloak.Next = ExchangeResult.Denied;
        _marks.Fault = new TimeoutException("store down");

        Assert.Equal(ValidationStatus.Forbidden, (await ValidateAsync()).Status);
        Assert.False(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task A_request_that_waited_on_the_refresh_which_revoked_the_session_is_forbidden()
    {
        SeedRefreshing(TimeSpan.FromSeconds(60));
        _lock.HeldElsewhere = true;
        _store.OnGet = key =>
        {
            if (key == Key && _store.Sessions.ContainsKey(Key) && _lock.Keys.Count >= 2)
            {
                _marks.Marks[Key] = TimeSpan.FromMinutes(15);
                _store.Sessions.TryRemove(Key, out _);
            }
        };

        Assert.Equal(ValidationStatus.Forbidden, (await ValidateAsync()).Status);
    }
}
