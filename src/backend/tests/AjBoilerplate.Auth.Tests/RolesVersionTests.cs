using System.Net;
using System.Security.Cryptography;
using System.Text;
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

public sealed class RolesVersionTests
{
    private static readonly DateTimeOffset Exp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly RolesVersion _sut = new(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));

    private string? For(string sub, DateTimeOffset exp, params string[] roles) =>
        _sut.For(Jwt.Create(sub, exp, TestConfig.ClientId, roles), TestConfig.ClientId);

    [Fact]
    public void It_is_sixteen_lowercase_hex_characters()
    {
        Assert.Matches("^[0-9a-f]{16}$", For("u1", Exp, "requester"));
    }

    [Fact]
    public void It_changes_when_the_roles_change()
    {
        Assert.NotEqual(For("u1", Exp, "requester"), For("u1", Exp, "approver"));
        Assert.NotEqual(For("u1", Exp, "requester"), For("u1", Exp, "requester", "approver"));
    }

    [Fact]
    public void It_is_stable_across_order_duplicates_renewal_and_person()
    {
        var baseline = For("u1", Exp, "requester", "approver");

        Assert.Equal(baseline, For("u1", Exp, "approver", "requester"));
        Assert.Equal(baseline, For("u1", Exp, "approver", "requester", "approver"));
        Assert.Equal(baseline, For("u1", Exp.AddMinutes(5), "requester", "approver"));
        Assert.Equal(baseline, For("someone-else", Exp, "requester", "approver"));
    }

    [Fact]
    public void It_reads_the_flat_roles_claim_the_dev_realm_issues()
    {
        var flat = _sut.For(Jwt.CreateFlat("u1", Exp, TestConfig.ClientId, "requester"), TestConfig.ClientId);

        Assert.Equal(For("u1", Exp, "requester"), flat);
    }

    [Fact]
    public void The_same_roles_on_another_client_give_another_value()
    {
        var ebudgeting = _sut.For(Jwt.Create("u1", Exp, "ebudgeting", "admin"), "ebudgeting");
        var cad = _sut.For(Jwt.Create("u1", Exp, "cad", "admin"), "cad");

        Assert.NotEqual(ebudgeting, cad);
    }

    [Fact]
    public void No_role_for_the_client_or_an_unreadable_token_gives_no_value()
    {
        Assert.Null(For("u1", Exp));
        Assert.Null(_sut.For(Jwt.Create("u1", Exp, "cad", "requester"), TestConfig.ClientId));
        Assert.Null(_sut.For("not-a-jwt", TestConfig.ClientId));
        Assert.Null(_sut.For(null, TestConfig.ClientId));
    }

    [Fact]
    public void It_does_not_carry_the_roles_the_client_or_the_subject()
    {
        const string Subject = "6f1c2e0a-subject";
        var value = For(Subject, Exp, "requester")!;

        Assert.DoesNotContain("requester", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestConfig.ClientId, value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Subject, value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_is_keyed_so_a_role_set_cannot_be_recovered_by_hashing_guesses()
    {
        var guess = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"[\"{TestConfig.ClientId}\",\"requester\"]")))[..16];
        var otherKey = new RolesVersion(new SessionTokenProtector(SessionTokenProtector.GenerateKey()));

        var value = For("u1", Exp, "requester");

        Assert.NotEqual(guess, value);
        Assert.NotEqual(otherKey.For(Jwt.Create("u1", Exp, TestConfig.ClientId, "requester"), TestConfig.ClientId), value);
    }
}

[Trait("Category", "Integration")]
public sealed class RolesVersionEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Subject = "kc-user";

    private readonly InMemorySessionStore _sessions = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly CapturingProvider _logs = new();
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
        builder.Services.AddSingleton(new PortalSessionId(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
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

    private string Seed(params string[] roles)
    {
        var id = SessionIds.NewRandom();
        var exp = DateTimeOffset.UtcNow.AddMinutes(30);
        _sessions.Sessions[SessionIds.StorageKey(id)] = new AuthSession(
            Jwt.Create(Subject, exp, TestConfig.ClientId, roles), exp, "rt-1", Subject, TestConfig.ClientId, false, DateTimeOffset.UtcNow.AddMinutes(-5));
        _sessions.Index.GetOrAdd(Subject, _ => []).Add(SessionIds.StorageKey(id));
        return id;
    }

    private async Task<HttpResponseMessage> ValidateAsync(string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/validate");
        request.Headers.Add("Cookie", $"{TestConfig.SessionCookie}={sessionId}");
        return await _http!.SendAsync(request);
    }

    private static string? Version(HttpResponseMessage response) =>
        response.Headers.TryGetValues(AuthEndpoints.RolesVersionHeader, out var values) ? values.Single() : null;

    [Fact]
    public async Task Validate_carries_the_header_and_it_is_stable_while_the_roles_are()
    {
        var sid = Seed("requester");

        using var first = await ValidateAsync(sid);
        using var second = await ValidateAsync(sid);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Matches("^[0-9a-f]{16}$", Version(first));
        Assert.Equal(Version(first), Version(second));
    }

    [Fact]
    public async Task The_header_changes_after_a_role_change_and_the_session_is_kept()
    {
        var sid = Seed("requester");
        using var before = await ValidateAsync(sid);

        using (var scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ForceRefreshHandler>().HandleAsync(Subject, CancellationToken.None);
        }

        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, DateTimeOffset.UtcNow.AddMinutes(30), TestConfig.ClientId, "approver"));
        using var after = await ValidateAsync(sid);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, _keycloak.Calls);
        Assert.NotNull(Version(before));
        Assert.NotNull(Version(after));
        Assert.NotEqual(Version(before), Version(after));
    }

    private async Task<HttpResponseMessage> RolesVersionEndpointAsync(string? sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/roles-version");
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"{TestConfig.SessionCookie}={sessionId}");
        }

        return await _http!.SendAsync(request);
    }

    [Fact]
    public async Task The_endpoint_returns_exactly_the_fingerprint_validate_computes_as_no_store_json_and_no_header()
    {
        var sid = Seed("requester");

        using var validate = await ValidateAsync(sid);
        using var response = await RolesVersionEndpointAsync(sid);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($$"""{"rolesVersion":"{{Version(validate)}}"}""", body);
        Assert.Matches("^[0-9a-f]{16}$", Version(validate));
        Assert.Null(Version(response));
    }

    [Fact]
    public async Task The_endpoint_value_changes_after_a_role_change_and_reads_do_not_touch_the_session()
    {
        var sid = Seed("requester");
        using var before = await RolesVersionEndpointAsync(sid);

        await ForceRefreshAsync();
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, DateTimeOffset.UtcNow.AddMinutes(30), TestConfig.ClientId, "approver"));
        using var after = await RolesVersionEndpointAsync(sid);
        using var again = await RolesVersionEndpointAsync(sid);

        Assert.NotEqual(await before.Content.ReadAsStringAsync(), await after.Content.ReadAsStringAsync());
        Assert.Equal(await after.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(1, _keycloak.Calls);   // the second read reused the refreshed session: no further exchange
        Assert.Single(_sessions.Sessions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-session")]
    public async Task The_endpoint_is_401_without_a_live_session_and_still_no_store(string? cookie)
    {
        using var response = await RolesVersionEndpointAsync(cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("rolesVersion", await response.Content.ReadAsStringAsync());
    }

    private static string? Access(HttpResponseMessage response) =>
        response.Headers.TryGetValues(AuthEndpoints.AccessHeader, out var values) ? string.Join(",", values) : null;

    private async Task ForceRefreshAsync()
    {
        using var scope = _app!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ForceRefreshHandler>().HandleAsync(Subject, CancellationToken.None);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("no_client_role")]
    public async Task A_definitive_no_role_is_403_with_the_revoked_marker_and_stays_so_for_that_cookie(string verdict)
    {
        var sid = Seed("requester");
        await ForceRefreshAsync();
        _keycloak.Next = verdict == "denied"
            ? ExchangeResult.Denied
            : new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, DateTimeOffset.UtcNow.AddMinutes(30), TestConfig.ClientId));

        using var first = await ValidateAsync(sid);
        using var again = await ValidateAsync(sid);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal("revoked", Access(first));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal("revoked", Access(again));
        Assert.Null(Version(first));
    }

    [Fact]
    public async Task An_infrastructure_fault_is_503_without_the_marker()
    {
        var sid = SessionIds.NewRandom();
        var exp = DateTimeOffset.UtcNow.AddSeconds(-10);   // nothing left to serve: the fault surfaces as 503
        _sessions.Sessions[SessionIds.StorageKey(sid)] = new AuthSession(
            Jwt.Create(Subject, exp, TestConfig.ClientId, "requester"), exp, "rt-1", Subject, TestConfig.ClientId, false, DateTimeOffset.UtcNow.AddMinutes(-5));
        _keycloak.Next = ExchangeResult.Transient;

        using var response = await ValidateAsync(sid);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(Access(response));
    }

    [Fact]
    public async Task A_401_and_a_200_carry_no_marker()
    {
        using var unauthenticated = await ValidateAsync(SessionIds.NewRandom());
        using var ok = await ValidateAsync(Seed("requester"));

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Null(Access(unauthenticated));
        Assert.Null(Access(ok));
    }

    [Fact]
    public async Task No_header_on_a_401()
    {
        using var response = await ValidateAsync(SessionIds.NewRandom());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(Version(response));
    }

    [Fact]
    public async Task Neither_the_fingerprint_nor_the_roles_reach_any_log_line()
    {
        var sid = Seed("requester-marker-role");
        using var response = await ValidateAsync(sid);
        var version = Version(response)!;

        Assert.Contains(_logs.Entries, e => e.Message.Contains("/auth/validate", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(version, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("requester-marker-role", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(Subject, StringComparison.Ordinal));
    }
}
