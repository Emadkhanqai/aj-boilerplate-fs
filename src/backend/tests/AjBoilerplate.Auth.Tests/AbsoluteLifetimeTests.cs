using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class AbsoluteLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly FakeLock _lock = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly SessionValidator _sut;
    private readonly string _sid = SessionIds.NewRandom();

    public AbsoluteLifetimeTests()
    {
        var options = TestConfig.Options();
        _sut = new SessionValidator(_store, _lock, _entra, _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());
    }

    private string Key => SessionIds.StorageKey(_sid);

    private void Seed(DateTimeOffset signedInAt)
    {
        _store.Sessions[Key] = new AuthSession(
            Jwt.Create("kc-user", Now.AddMinutes(10), TestConfig.ClientId, "requester"),
            Now.AddMinutes(10),
            "rt-1",
            "kc-user",
            TestConfig.ClientId,
            false,
            signedInAt);
        _store.Index.GetOrAdd("kc-user", _ => []).Add(Key);
    }

    [Fact]
    public async Task A_session_signed_in_more_than_12_hours_ago_is_401_and_removed()
    {
        Seed(Now - TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1));

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.Null(result.Jwt);
        Assert.False(_store.Sessions.ContainsKey(Key));
        Assert.Equal(0, _entra.Refreshes); // ended by policy, not by an attempted refresh
    }

    [Fact]
    public async Task A_session_just_inside_12_hours_is_still_served()
    {
        Seed(Now - TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));

        var result = await _sut.ValidateAsync(_sid, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.True(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public async Task Activity_does_not_extend_the_absolute_lifetime()
    {
        Seed(Now - TimeSpan.FromHours(11));
        Assert.Equal(ValidationStatus.Ok, (await _sut.ValidateAsync(_sid, default)).Status);

        _time.Now = Now + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);
        _store.Sessions[Key] = _store.Sessions[Key] with { JwtExpiresAt = _time.Now.AddMinutes(10) };

        Assert.Equal(ValidationStatus.Unauthenticated, (await _sut.ValidateAsync(_sid, default)).Status);
    }

    [Fact]
    public async Task A_session_with_no_recorded_sign_in_time_is_kept_and_its_clock_starts_at_first_sight()
    {
        Seed(DateTimeOffset.UnixEpoch); // what RedisSessionStore yields for an absent `created` field

        Assert.Equal(ValidationStatus.Ok, (await _sut.ValidateAsync(_sid, default)).Status);
        Assert.Equal(Now, _store.Sessions[Key].CreatedAt);

        _time.Now = Now + TimeSpan.FromHours(12);
        _store.Sessions[Key] = _store.Sessions[Key] with { JwtExpiresAt = _time.Now.AddMinutes(10) };
        Assert.Equal(ValidationStatus.Unauthenticated, (await _sut.ValidateAsync(_sid, default)).Status);
    }

    [Fact]
    public async Task The_cap_is_configurable()
    {
        var options = TestConfig.Options();
        options.Session.AbsoluteLifetime = TimeSpan.FromHours(1);
        var sut = new SessionValidator(_store, _lock, _entra, _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());
        Seed(Now - TimeSpan.FromHours(2));

        Assert.Equal(ValidationStatus.Unauthenticated, (await sut.ValidateAsync(_sid, default)).Status);
        Assert.Equal(TimeSpan.FromHours(12), new AjBoilerplate.Auth.Configuration.AuthSessionOptions().AbsoluteLifetime);
    }

    [Fact]
    public async Task Ending_one_session_leaves_the_same_persons_other_sessions_alone()
    {
        var otherKey = SessionIds.StorageKey(SessionIds.NewRandom());
        _store.Sessions[otherKey] = new AuthSession(
            Jwt.Create("kc-user", Now.AddMinutes(10), TestConfig.ClientId, "requester"),
            Now.AddMinutes(10), "rt-2", "kc-user", TestConfig.ClientId, false, Now - TimeSpan.FromHours(1));
        Seed(Now - TimeSpan.FromHours(13));

        Assert.Equal(ValidationStatus.Unauthenticated, (await _sut.ValidateAsync(_sid, default)).Status);
        Assert.True(_store.Sessions.ContainsKey(otherKey));
    }
}
