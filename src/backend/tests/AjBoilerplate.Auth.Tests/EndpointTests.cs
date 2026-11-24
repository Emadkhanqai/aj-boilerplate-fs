using System.Net;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;

namespace AjBoilerplate.Auth.Tests;

public sealed class AdminEndpointTests
{
    private const string Key = "a-long-random-admin-key";
    private const string Path = "/internal/admin/users/kc-user/refresh";

    [Fact]
    public async Task With_no_key_configured_the_surface_does_not_exist_even_for_a_caller_that_sends_one()
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, Path, null, (AdminEndpoints.ApiKeyHeader, Key));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("a-long-random-admin-key-and-more")]
    [InlineData("a-long-random-admin-ke")]
    public async Task A_missing_or_wrong_key_is_401_and_flags_nothing(string? supplied)
    {
        await using var host = await EndpointHost.StartAsync(o => o.Admin.ApiKey = Key);
        var sid = host.Seed();

        using var response = supplied is null
            ? await host.SendAsync(HttpMethod.Post, Path)
            : await host.SendAsync(HttpMethod.Post, Path, null, (AdminEndpoints.ApiKeyHeader, supplied));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }

    [Fact]
    public async Task The_right_key_flags_every_session_of_that_subject_and_no_other()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Admin.ApiKey = Key);
        var mine = host.Seed();
        var other = host.Seed();
        host.Sessions.Sessions[SessionIds.StorageKey(other)] = host.Sessions.Sessions[SessionIds.StorageKey(other)] with { Subject = "someone-else" };
        host.Sessions.Index[EndpointHost.Subject].Remove(SessionIds.StorageKey(other));
        host.Sessions.Index["someone-else"] = [SessionIds.StorageKey(other)];

        using var response = await host.SendAsync(HttpMethod.Post, Path, null, (AdminEndpoints.ApiKeyHeader, Key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"flagged":1}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(host.Sessions.Sessions[SessionIds.StorageKey(mine)].ForceRefresh);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(other)].ForceRefresh);
    }

    [Fact]
    public async Task An_oversized_subject_is_400()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Admin.ApiKey = Key);

        using var response = await host.SendAsync(HttpMethod.Post, $"/internal/admin/users/{new string('x', 300)}/refresh", null, (AdminEndpoints.ApiKeyHeader, Key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_key_and_the_subject_never_reach_a_log_line()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Admin.ApiKey = Key);
        host.Seed();

        using var ok = await host.SendAsync(HttpMethod.Post, Path, null, (AdminEndpoints.ApiKeyHeader, Key));
        using var bad = await host.SendAsync(HttpMethod.Post, Path, null, (AdminEndpoints.ApiKeyHeader, "wrong-key-value"));

        Assert.NotEmpty(host.Logs.Entries); // control: the logger saw this run
        var all = string.Join("\n", host.Logs.Entries.Select(e => e.Message + string.Join(",", e.Fields.Values)));
        Assert.DoesNotContain(Key, all, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-key-value", all, StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointHost.Subject, all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("same", "same", true)]
    [InlineData("same", "Same", false)]
    [InlineData("", "same", false)]
    [InlineData("same", "", false)]
    public void Keys_match_only_when_identical(string supplied, string expected, bool match) =>
        Assert.Equal(match, AdminEndpoints.KeyMatches(supplied, expected));
}

public sealed class LogoutAndLoginEndpointTests
{
    [Fact]
    public async Task A_cross_origin_logout_is_403_and_keeps_the_session()
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed();

        using var response = await host.SendAsync(HttpMethod.Post, "/auth/logout", sid, ("Origin", "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(host.Sessions.Sessions.ContainsKey(SessionIds.StorageKey(sid)));
    }

    [Fact]
    public async Task A_same_origin_logout_ends_the_session_clears_the_cookie_and_lands_on_the_logged_out_page()
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed();

        using var response = await host.SendAsync(HttpMethod.Post, "/auth/logout", sid, ("Origin", TestConfig.Origin));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/logged-out", response.Headers.Location?.OriginalString);
        Assert.False(host.Sessions.Sessions.ContainsKey(SessionIds.StorageKey(sid)));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-app_sid=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sign-in", "/auth/login?prompt=login")]
    [InlineData("https://evil.example", "/logged-out")]
    [InlineData("", "/logged-out")]
    public async Task Next_chooses_between_two_constant_targets_and_never_echoes_the_caller(string next, string expected)
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, $"/auth/logout?next={Uri.EscapeDataString(next)}", null, ("Sec-Fetch-Site", "same-origin"));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_redirects_to_the_identity_provider_and_binds_the_browser_with_a_host_cookie()
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/login?rd=/budgets");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://login.example/authorize", response.Headers.Location?.AbsoluteUri, StringComparison.Ordinal);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-app_login=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new Uri("https://app.example.com/auth/callback"), host.Entra.LastRedirectUri);
    }

    [Fact]
    public async Task With_secure_cookies_off_the_host_prefix_and_the_secure_flag_are_dropped()
    {
        await using var host = await EndpointHost.StartAsync(o =>
        {
            o.Cookies.Secure = false;
            o.PublicOrigin = "http://localhost:4200";
        });

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/login");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("app_login=", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CallbackAndValidateEndpointTests
{
    [Fact]
    public async Task A_transient_upstream_during_the_callback_is_a_503_page_and_never_a_redirect_or_a_cookie()
    {
        await using var host = await EndpointHost.StartAsync();
        host.Keycloak.Next = ExchangeResult.Transient;

        using var login = await host.SendAsync(HttpMethod.Get, "/auth/login");
        var binding = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];
        var state = host.Logins.States.Keys.Single();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/callback?code=c&state={state}");
        request.Headers.Add("Cookie", $"__Host-app_login={binding}");
        using var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-app_sid=", StringComparison.Ordinal) && !c.Contains("1970", StringComparison.Ordinal));
        Assert.Empty(host.Sessions.Sessions);
    }

    [Fact]
    public async Task A_no_role_user_gets_a_403_page_not_a_redirect()
    {
        await using var host = await EndpointHost.StartAsync();
        host.Keycloak.Next = ExchangeResult.Denied;

        using var login = await host.SendAsync(HttpMethod.Get, "/auth/login");
        var binding = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/callback?code=c&state={host.Logins.States.Keys.Single()}");
        request.Headers.Add("Cookie", $"__Host-app_login={binding}");
        using var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("No access", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_callback_sets_the_session_cookie_and_redirects_to_the_return_path()
    {
        await using var host = await EndpointHost.StartAsync();
        host.Keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(EndpointHost.Subject, DateTimeOffset.UtcNow.AddMinutes(5), TestConfig.ClientId, "requester"));

        using var login = await host.SendAsync(HttpMethod.Get, "/auth/login?rd=/budgets/1");
        var binding = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/callback?code=c&state={host.Logins.States.Keys.Single()}");
        request.Headers.Add("Cookie", $"__Host-app_login={binding}");
        using var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/budgets/1", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-app_sid=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Single(host.Sessions.Sessions);
    }

    [Fact]
    public async Task An_infrastructure_fault_on_validate_is_503_with_the_failure_marker_and_no_jwt_and_keeps_the_session()
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed(TimeSpan.FromSeconds(-10));
        host.Entra.NextRefresh = EntraTokenResult.Transient;

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/validate", sid);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("auth", Assert.Single(response.Headers.GetValues("X-Usp-Failure")));
        Assert.False(response.Headers.Contains("X-Auth-Jwt"));
        Assert.False(response.Headers.Contains("X-Usp-Access"));
        Assert.True(host.Sessions.Sessions.ContainsKey(SessionIds.StorageKey(sid)));
    }

    [Fact]
    public async Task A_live_session_gets_the_three_contract_headers_and_the_response_is_not_cacheable()
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/validate", sid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(response.Headers.GetValues("X-Auth-Jwt").Single());
        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Usp-Session-Id").Single());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", response.Headers.GetValues("X-Usp-Session-Started-At").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}

public sealed class ProgramCompositionTests
{
    internal static string[] Args(params string[] overrides)
    {
        var settings = new Dictionary<string, string>
        {
            ["Auth:PublicOrigin"] = "https://app.example.com",
            ["Auth:Entra:Authority"] = "https://tenant.example.com/policy",
            ["Auth:Entra:ClientId"] = "entra-client",
            ["Auth:Entra:ClientSecret"] = "entra-secret",
            ["Auth:Keycloak:BaseUrl"] = "https://keycloak.example",
            ["Auth:Keycloak:SubjectIssuer"] = "entra",
            ["Auth:Portal:ClientId"] = "my-app",
            ["Auth:Portal:ClientSecret"] = "kc-secret",
            ["Auth:Session:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false",
            ["ConnectionStrings:AuthDb"] = "Server=127.0.0.1,1;Database=auth;User Id=u;Password=p;TrustServerCertificate=true",
        };
        foreach (var o in overrides)
        {
            var split = o.Split('=', 2);
            settings[split[0]] = split[1];
        }

        return [.. settings.Select(s => $"--{s.Key}={s.Value}")];
    }

    private static List<string> Routes(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText!)];

    [Fact]
    public async Task The_service_exposes_exactly_the_contract_routes()
    {
        await using var app = Program.Build(Args());

        var routes = Routes(app);

        Assert.Equal(
            [
                "/auth/callback",
                "/auth/events",
                "/auth/handoff",
                "/auth/handoff/redeem",
                "/auth/handoff/redeem",
                "/auth/login",
                "/auth/logout",
                "/auth/no-access",
                "/auth/roles-version",
                "/auth/validate",
                "/healthz",
                "/internal/admin/groups",
                "/internal/admin/members",
                "/internal/admin/users",
                "/internal/admin/users",
                "/internal/admin/users/{subject}/disable",
                "/internal/admin/users/{subject}/enable",
                "/internal/admin/users/{subject}/groups",
                "/internal/admin/users/{subject}/groups",
                "/internal/admin/users/{subject}/refresh",
                "/readyz",
            ],
            routes.Order(StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData("ConnectionStrings:Redis")]
    [InlineData("ConnectionStrings:AuthDb")]
    public void A_missing_connection_string_stops_start_up(string name)
    {
        var args = Args().Where(a => !a.StartsWith($"--{name}=", StringComparison.Ordinal)).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => Program.Build(args));
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Auth:PublicOrigin=")]
    [InlineData("Auth:Entra:ClientSecret=")]
    [InlineData("Auth:Portal:ClientSecret=")]
    [InlineData("Auth:Keycloak:SubjectIssuer=")]
    [InlineData("Auth:Session:EncryptionKey=AAAA")]
    [InlineData("Auth:Cookies:Secure=false")]
    public void Missing_or_invalid_required_configuration_stops_start_up(string overrideSetting) =>
        Assert.Throws<OptionsValidationException>(() => Program.Build(Args(overrideSetting)));

    [Fact]
    public async Task The_valid_configuration_builds()
    {
        await using var app = Program.Build(Args());

        var options = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal("https://app.example.com", options.PublicOrigin); // control: the arguments reached the options
        Assert.Equal("my-app", options.Portal.ClientId);
    }

    [Theory]
    [InlineData("abc-123_X", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("line\nbreak", false)]
    public void Only_short_id_shaped_request_ids_are_trusted(string value, bool expected) =>
        Assert.Equal(expected, Program.IsIdShaped(value));
}

public sealed class ProgramLoggingTests
{
    private sealed class Capture : Serilog.Core.ILogEventSink
    {
        public List<string> Lines { get; } = [];

        public void Emit(Serilog.Events.LogEvent logEvent) => Lines.Add(logEvent.RenderMessage());
    }

    [Fact]
    public void The_frameworks_request_starting_line_which_carries_the_query_string_is_not_written()
    {
        var capture = new Capture();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        using var logger = Program.ConfigureLogger(new Serilog.LoggerConfiguration(), config).WriteTo.Sink(capture).CreateLogger();
        var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(logger);

        // The same line and category the framework writes at Information for every request.
        factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
            .LogInformation("Request starting HTTP/1.1 GET http://x/auth/callback?code=SECRET-CODE&state=SECRET-STATE");
        factory.CreateLogger("AjBoilerplate.Auth.Anything").LogInformation("control line");

        Assert.Equal(["control line"], capture.Lines);
    }
}
