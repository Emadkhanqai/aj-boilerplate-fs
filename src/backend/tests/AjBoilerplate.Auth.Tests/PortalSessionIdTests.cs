using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class PortalSessionIdTests
{
    private readonly PortalSessionId _sut = new(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));

    [Fact]
    public void It_is_thirty_two_lowercase_hex_characters()
    {
        Assert.Matches("^[0-9a-f]{32}$", _sut.For(SessionIds.StorageKey(SessionIds.NewRandom())));
    }

    [Fact]
    public void One_session_gives_one_id_and_two_sessions_give_two()
    {
        var a = SessionIds.StorageKey(SessionIds.NewRandom());
        var b = SessionIds.StorageKey(SessionIds.NewRandom());

        Assert.Equal(_sut.For(a), _sut.For(a));
        Assert.NotEqual(_sut.For(a), _sut.For(b));
    }

    [Fact]
    public void It_is_keyed_so_another_server_key_gives_another_id()
    {
        var key = SessionIds.StorageKey(SessionIds.NewRandom());
        var other = new PortalSessionId(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));

        Assert.NotEqual(_sut.For(key), other.For(key));
    }

    [Fact]
    public void It_carries_neither_the_cookie_nor_the_storage_key()
    {
        var cookie = SessionIds.NewRandom();
        var key = SessionIds.StorageKey(cookie);
        var id = _sut.For(key);

        Assert.DoesNotContain(cookie, id, StringComparison.Ordinal);
        Assert.DoesNotContain(key, id, StringComparison.Ordinal);
        Assert.DoesNotContain(id, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(id, key, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_in_time_is_utc_to_the_whole_second()
    {
        var doha = new DateTimeOffset(2026, 10, 21, 12, 15, 42, TimeSpan.FromHours(3)).AddTicks(1234567);

        Assert.Equal("2026-10-21T09:15:42Z", PortalSessionId.FormatStartedAt(doha));
    }

    [Fact]
    public void The_sign_in_time_is_the_same_whether_Redis_or_SQL_answered()
    {
        var sql = new DateTimeOffset(2026, 10, 21, 9, 15, 42, TimeSpan.Zero).AddTicks(9_999_999);
        var redis = DateTimeOffset.FromUnixTimeSeconds(sql.ToUnixTimeSeconds());

        Assert.Equal(PortalSessionId.FormatStartedAt(redis), PortalSessionId.FormatStartedAt(sql));
    }
}

public sealed class SessionIdentityAcrossRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedIn = Now - TimeSpan.FromHours(1);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly SessionValidator _sut;
    private readonly PortalSessionId _ids = new(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));

    public SessionIdentityAcrossRefreshTests()
    {
        var options = TestConfig.Options();
        _sut = new SessionValidator(_store, new FakeLock(), new FakeEntra(), _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());
    }

    private string Seed(DateTimeOffset createdAt)
    {
        var sid = SessionIds.NewRandom();
        var exp = Now + TimeSpan.FromMinutes(10);
        _store.Sessions[SessionIds.StorageKey(sid)] = new AuthSession(
            Jwt.Create("kc-user", exp, TestConfig.ClientId, "requester"), exp, "rt-1", "kc-user", TestConfig.ClientId, false, createdAt);
        return sid;
    }

    [Fact]
    public async Task A_renewal_replaces_the_jwt_and_keeps_the_id_and_the_sign_in_time()
    {
        var sid = Seed(SignedIn);
        var before = await _sut.ValidateAsync(sid, default);

        _time.Now = Now + TimeSpan.FromMinutes(9);
        var renewed = Jwt.Create("kc-user", _time.Now + TimeSpan.FromMinutes(10), TestConfig.ClientId, "requester");
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, renewed);
        var after = await _sut.ValidateAsync(sid, default);

        Assert.Equal(1, _keycloak.Calls);
        Assert.NotEqual(before.Jwt, after.Jwt);
        Assert.Equal(renewed, after.Jwt);

        Assert.Equal(SessionIds.StorageKey(sid), before.SessionKey);
        Assert.Equal(_ids.For(before.SessionKey!), _ids.For(after.SessionKey!));
        Assert.Equal(SignedIn, before.SignedInAt);
        Assert.Equal(SignedIn, after.SignedInAt);
    }

    [Fact]
    public async Task A_session_with_no_recorded_sign_in_reports_the_time_that_was_stored()
    {
        var sid = Seed(DateTimeOffset.UnixEpoch);

        var first = await _sut.ValidateAsync(sid, default);
        _time.Now = Now + TimeSpan.FromSeconds(30);
        var second = await _sut.ValidateAsync(sid, default);

        Assert.Equal(_store.Sessions[SessionIds.StorageKey(sid)].CreatedAt, first.SignedInAt);
        Assert.Equal(first.SignedInAt, second.SignedInAt);
        Assert.Equal(Now, first.SignedInAt);
    }

    [Fact]
    public async Task When_two_first_validates_race_the_stamp_that_survived_is_reported()
    {
        var sid = Seed(DateTimeOffset.UnixEpoch);
        var racer = Now - TimeSpan.FromSeconds(5);
        var options = TestConfig.Options();
        var sut = new SessionValidator(
            new RacingStampStore(_store, racer), new FakeLock(), new FakeEntra(), _keycloak, TestConfig.Wrap(options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());

        var result = await sut.ValidateAsync(sid, default);

        Assert.Equal(racer, _store.Sessions[SessionIds.StorageKey(sid)].CreatedAt);
        Assert.Equal(racer, result.SignedInAt);
    }

    private sealed class RacingStampStore(InMemorySessionStore inner, DateTimeOffset racer) : ISessionStore
    {
        public Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken) => inner.GetAsync(sessionKey, cancellationToken);

        public Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken) =>
            inner.CreateAsync(sessionKey, session, idleTimeout, cancellationToken);

        public Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken) =>
            inner.TouchAsync(sessionKey, subject, idleTimeout, cancellationToken);

        public Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken) =>
            inner.UpdateTokensAsync(sessionKey, jwt, jwtExpiresAt, refreshToken, cancellationToken);

        public Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken) =>
            inner.UpdateRefreshTokenAsync(sessionKey, refreshToken, cancellationToken);

        public Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken) => inner.MarkForceRefreshAsync(sessionKey, cancellationToken);

        public async Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken)
        {
            await inner.StampSignInTimeAsync(sessionKey, racer, cancellationToken);
            await inner.StampSignInTimeAsync(sessionKey, signedInAt, cancellationToken);
        }

        public Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken) => inner.DeleteAsync(sessionKey, subject, cancellationToken);

        public Task<bool> EndAsync(string sessionKey, string subject, CancellationToken cancellationToken) =>
            inner.EndAsync(sessionKey, subject, cancellationToken);

        public Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken) =>
            inner.GetSessionKeysForSubjectAsync(subject, cancellationToken);

        public Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken) =>
            inner.RemoveFromSubjectIndexAsync(subject, sessionKey, cancellationToken);
    }

    [Fact]
    public async Task Nothing_identifying_is_returned_when_validation_fails()
    {
        var result = await _sut.ValidateAsync(SessionIds.NewRandom(), default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.Null(result.SessionKey);
        Assert.Null(result.SignedInAt);
    }
}

[Trait("Category", "Integration")]
public sealed class PortalSessionIdEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Subject = "kc-session-user";

    private static readonly DateTimeOffset SignedIn =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600);

    private static readonly string SignedInText = PortalSessionId.FormatStartedAt(SignedIn);

    private readonly InMemorySessionStore _sessions = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly CapturingProvider _logs = new();
    private readonly PortalSessionId _ids = new(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft", LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
        builder.Logging.AddProvider(_logs);

        var options = TestConfig.Options();
        builder.Services.AddSingleton(TestConfig.Wrap(options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISessionStore>(_sessions);
        builder.Services.AddSingleton<ILoginStateStore>(new InMemoryLoginStore());
        builder.Services.AddSingleton<IDistributedLock>(new FakeLock());
        builder.Services.AddSingleton<IEntraClient>(new FakeEntra());
        builder.Services.AddSingleton<IKeycloakExchanger>(_keycloak);
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(new RolesVersion(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
        builder.Services.AddSingleton(_ids);
        builder.Services.AddSingleton<IRevokedSessionMarks>(new InMemoryRevokedSessionMarks());
        builder.Services.AddScoped<SessionValidator>();
        builder.Services.AddScoped<SignInService>();
        builder.Services.AddScoped<ForceRefreshHandler>();

        _app = builder.Build();
        _app.MapAuthEndpoints();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _logs.Dispose();
    }

    private string Seed(TimeSpan jwtLifetime)
    {
        var id = SessionIds.NewRandom();
        var exp = DateTimeOffset.UtcNow + jwtLifetime;
        _sessions.Sessions[SessionIds.StorageKey(id)] = new AuthSession(
            Jwt.Create(Subject, exp, TestConfig.ClientId, "requester"), exp, "rt-1", Subject, TestConfig.ClientId, false, SignedIn);
        _sessions.Index.GetOrAdd(Subject, _ => []).Add(SessionIds.StorageKey(id));
        return id;
    }

    private async Task<HttpResponseMessage> ValidateAsync(string sessionId, string? spoofedId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/validate");
        request.Headers.Add("Cookie", $"{TestConfig.SessionCookie}={sessionId}");
        if (spoofedId is not null)
        {
            request.Headers.Add(AuthEndpoints.SessionIdHeader, spoofedId);
            request.Headers.Add(AuthEndpoints.SessionStartedAtHeader, "1999-01-01T00:00:00Z");
        }

        return await _http!.SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static string? InjectedJwt(HttpResponseMessage response) => Header(response, AuthEndpoints.JwtHeader);

    private static string? Id(HttpResponseMessage response) => Header(response, AuthEndpoints.SessionIdHeader);

    private static string? StartedAt(HttpResponseMessage response) => Header(response, AuthEndpoints.SessionStartedAtHeader);

    [Fact]
    public async Task Both_headers_are_stable_across_a_renewal_of_the_jwt()
    {
        var sid = Seed(TimeSpan.FromMinutes(30));
        using var before = await ValidateAsync(sid);

        var key = SessionIds.StorageKey(sid);
        _sessions.Sessions[key] = _sessions.Sessions[key] with { ForceRefresh = true };
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, DateTimeOffset.UtcNow.AddMinutes(31), TestConfig.ClientId, "requester"));
        using var after = await ValidateAsync(sid);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, _keycloak.Calls);
        Assert.NotEqual(InjectedJwt(before), InjectedJwt(after));

        Assert.Matches("^[0-9a-f]{32}$", Id(before));
        Assert.Equal(Id(before), Id(after));
        Assert.Equal(SignedInText, StartedAt(before));
        Assert.Equal(StartedAt(before), StartedAt(after));
    }

    [Fact]
    public async Task Two_sessions_get_two_ids()
    {
        var first = Seed(TimeSpan.FromMinutes(30));
        var second = Seed(TimeSpan.FromMinutes(30));

        using var a = await ValidateAsync(first);
        using var b = await ValidateAsync(second);

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.NotEqual(Id(a), Id(b));
    }

    [Fact]
    public async Task The_id_is_the_hmac_of_the_cookie_derived_key_and_never_the_cookie()
    {
        var sid = Seed(TimeSpan.FromMinutes(30));
        using var response = await ValidateAsync(sid);
        var id = Id(response)!;

        Assert.Equal(_ids.For(SessionIds.StorageKey(sid)), id);
        Assert.DoesNotContain(sid, id, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionIds.StorageKey(sid), id, StringComparison.Ordinal);

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            foreach (var value in header.Value)
            {
                Assert.DoesNotContain(sid, value, StringComparison.Ordinal);
                Assert.DoesNotContain(SessionIds.StorageKey(sid), value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_client_supplied_copy_of_either_header_changes_nothing()
    {
        var sid = Seed(TimeSpan.FromMinutes(30));

        using var honest = await ValidateAsync(sid);
        using var spoofed = await ValidateAsync(sid, spoofedId: "0123456789abcdef0123456789abcdef");

        Assert.Equal(Id(honest), Id(spoofed));
        Assert.Equal(StartedAt(honest), StartedAt(spoofed));
        Assert.Equal(SignedInText, StartedAt(spoofed));
    }

    [Fact]
    public async Task Neither_header_on_a_401()
    {
        using var response = await ValidateAsync(SessionIds.NewRandom());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(Id(response));
        Assert.Null(StartedAt(response));
    }

    [Fact]
    public async Task Neither_the_id_nor_the_cookie_reaches_any_log_line()
    {
        var sid = Seed(TimeSpan.FromMinutes(30));
        using var response = await ValidateAsync(sid);
        var id = Id(response)!;

        bool Carries(CapturedEntry e, string needle) =>
            e.Message.Contains(needle, StringComparison.Ordinal)
            || e.Fields.Values.Any(v => v?.ToString()?.Contains(needle, StringComparison.Ordinal) == true);

        Assert.Contains(_logs.Entries, e => e.Message.Contains("/auth/validate", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => Carries(e, id));
        Assert.DoesNotContain(_logs.Entries, e => Carries(e, sid));
        Assert.DoesNotContain(_logs.Entries, e => Carries(e, SessionIds.StorageKey(sid)));
    }
}
