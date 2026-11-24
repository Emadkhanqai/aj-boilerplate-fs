using System.Net;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.SessionEvents;
using AjBoilerplate.Auth.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AjBoilerplate.Auth.Tests;

public sealed class PortalRegistryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything.example.org")]
    [InlineData("not a host")]
    public void A_single_portal_installation_never_looks_at_the_host(string? host)
    {
        var registry = TestConfig.Portals(TestConfig.Options());

        var portal = registry.Resolve(host);

        Assert.False(registry.IsMultiPortal);
        Assert.Same(registry.Primary, portal);
        Assert.Equal(TestConfig.ClientId, portal!.ClientId);
        Assert.Equal(TestConfig.Host, portal.Host);
        Assert.Equal(TestConfig.Origin, portal.Origin);
        Assert.Equal("/logged-out", portal.LoggedOutPath);
    }

    [Fact]
    public void Portal_zero_keeps_the_whole_configured_origin_port_and_scheme_included()
    {
        var options = TestConfig.Options();
        options.PublicOrigin = "http://localhost:4200";

        var portal = TestConfig.Portals(options).Primary;

        Assert.Equal("localhost", portal.Host);
        Assert.Equal("http://localhost:4200", portal.Origin);
    }

    [Theory]
    [InlineData("other.example.com", TestConfig.OtherClientId)]
    [InlineData("OTHER.Example.COM", TestConfig.OtherClientId)]
    [InlineData("other.example.com:8443", TestConfig.OtherClientId)]
    [InlineData("other.example.com, proxy.internal", TestConfig.OtherClientId)]
    [InlineData("app.example.com", TestConfig.ClientId)]
    public void With_several_portals_the_host_chooses(string host, string clientId) =>
        Assert.Equal(clientId, TestConfig.Portals(TestConfig.TwoPortals()).Resolve(host)!.ClientId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown.example.org")]
    [InlineData("other.example.com.evil.example")]
    [InlineData("https://other.example.com")]
    [InlineData("other.example.com/path")]
    public void With_several_portals_an_unknown_or_malformed_host_is_nobody(string? host) =>
        Assert.Null(TestConfig.Portals(TestConfig.TwoPortals()).Resolve(host));

    [Fact]
    public void The_hand_off_key_is_an_exact_client_id_never_a_url_or_a_look_alike()
    {
        var registry = TestConfig.Portals(TestConfig.TwoPortals());

        Assert.Equal(TestConfig.OtherHost, registry.FindByClientId("other-app")!.Host);
        Assert.Equal(TestConfig.Host, registry.FindByClientId("my-app")!.Host);
        Assert.Null(registry.FindByClientId("OTHER-APP"));
        Assert.Null(registry.FindByClientId("https://other.example.com"));
        Assert.Null(registry.FindByClientId(null));
    }

    private static IReadOnlyList<string> Failures(Action<AuthOptions> configure)
    {
        var options = TestConfig.TwoPortals();
        options.Session.EncryptionKey = SessionTokenProtector.GenerateKey();
        configure(options);
        return new AuthOptionsValidator().Validate(null, options).Failures?.ToList() ?? [];
    }

    [Fact]
    public void A_valid_two_portal_configuration_has_no_failures() => Assert.Empty(Failures(_ => { }));

    [Fact]
    public void A_portal_host_that_is_not_a_bare_host_is_refused_naming_the_row()
    {
        Assert.Contains(Failures(o => o.Portals[0].Host = "https://other.example.com"), f => f.StartsWith("Auth:Portals:0:Host must be a bare DNS host name", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Portals[0].Host = "other.example.com:8443"), f => f.StartsWith("Auth:Portals:0:Host must be a bare DNS host name", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Portals[0].Host = string.Empty), f => f.StartsWith("Auth:Portals:0:Host must be a bare DNS host name", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicated_host_or_client_id_is_refused_including_portal_zeros_own()
    {
        Assert.Contains(Failures(o => o.Portals[0].Host = TestConfig.Host), f => f.Contains("Auth:Portals:0:Host 'app.example.com' is already used", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Portals[0].ClientId = TestConfig.ClientId), f => f.Contains("Auth:Portals:0:ClientId 'my-app' is already used", StringComparison.Ordinal));
    }

    [Fact]
    public void A_portal_without_a_secret_or_with_an_unsafe_logged_out_path_is_refused()
    {
        Assert.Contains(Failures(o => o.Portals[0].ClientSecret = string.Empty), f => f.StartsWith("Auth:Portals:0:ClientId and ClientSecret are required", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Portals[0].LoggedOutPath = "//evil.example"), f => f.StartsWith("Auth:Portals:0:LoggedOutPath must be a local absolute path", StringComparison.Ordinal));
    }

    [Fact]
    public void The_events_and_admin_pairs_are_both_or_neither_and_managed_paths_are_group_paths()
    {
        Assert.Contains(Failures(o => o.Events.Audience = "https://auth.example.com"), f => f.StartsWith("Auth:Events:Audience and Auth:Events:PushServiceAccount", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Admin.Audience = "https://auth.example.com"), f => f.StartsWith("Auth:Admin:Audience and Auth:Admin:AllowedCallers", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Admin.Keycloak.ClientId = "directory-admin"), f => f.StartsWith("Auth:Admin:Keycloak:ClientId and ClientSecret", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Admin.ManagedGroupPaths.Add("my-app")), f => f.Contains("must be a Keycloak group path", StringComparison.Ordinal));
        Assert.Contains(Failures(o => o.Admin.ManagedGroupPaths.Add("/my-app/")), f => f.Contains("must be a Keycloak group path", StringComparison.Ordinal));
        Assert.Empty(Failures(o => o.Admin.ManagedGroupPaths.Add("/my-app")));
    }

    [Fact]
    public void A_plain_http_admin_keycloak_url_is_refused_outside_development_because_it_receives_a_client_secret() =>
        Assert.Contains(Failures(o => o.Admin.Keycloak.BaseUrl = "http://keycloak.internal"), f => f.StartsWith("Auth:Admin:Keycloak:BaseUrl is http.", StringComparison.Ordinal));
}

/// <summary>The service as <c>Program.Build</c> composes it from configuration, with the new optional pieces on and off.</summary>
public sealed class PortalCompositionTests
{
    private static string[] Args(params string[] overrides) => ProgramCompositionTests.Args(overrides);

    [Fact]
    public async Task By_default_it_is_a_single_portal_with_no_directory_no_receiver_and_the_events_push_off()
    {
        await using var app = Program.Build(Args());
        using var scope = app.Services.CreateScope();

        var registry = app.Services.GetRequiredService<PortalRegistry>();
        Assert.False(registry.IsMultiPortal);
        Assert.IsType<NotConfiguredDirectoryAdmin>(app.Services.GetRequiredService<IDirectoryAdmin>());
        Assert.False(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.Events.IsEnabled);
        Assert.False(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.Admin.IsEnabled);
        Assert.IsType<HttpSessionEndedSender>(app.Services.GetRequiredService<ISessionEndedSender>());
        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is SessionEndedDispatchService);

        // Everything a request needs resolves.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<HandoffService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UserAdminService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SignInService>());
    }

    [Fact]
    public async Task Configuration_binds_further_portals_the_admin_directory_and_the_session_events()
    {
        await using var app = Program.Build(Args(
            "Auth:Portals:0:Host=other.example.com",
            "Auth:Portals:0:ClientId=other-app",
            "Auth:Portals:0:ClientSecret=kc-secret-2",
            "Auth:Portals:0:DisplayName=Other App",
            "Auth:Portals:0:SessionEvents:Url=https://other-api.example.com/api/integration/auth-backend/session-ended",
            "Auth:Portals:0:SessionEvents:Key=a-shared-key",
            "Auth:Portal:SessionEvents:Url=https://api.example.com/api/integration/auth-backend/session-ended",
            "Auth:Admin:ApiKey=a-long-random-admin-key",
            "Auth:Admin:ManagedGroupPaths:0=/my-app",
            "Auth:Admin:Keycloak:ClientId=directory-admin",
            "Auth:Admin:Keycloak:ClientSecret=directory-secret",
            "Auth:Events:Audience=https://auth.example.com",
            "Auth:Events:PushServiceAccount=push@my-project.iam.gserviceaccount.com"));

        var registry = app.Services.GetRequiredService<PortalRegistry>();
        Assert.True(registry.IsMultiPortal);
        var other = registry.FindByClientId("other-app")!;
        Assert.Equal("other.example.com", other.Host);
        Assert.Equal("https://other.example.com", other.Origin);
        Assert.Equal("Other App", other.DisplayName);
        Assert.True(other.SessionEvents.UsesKey);
        Assert.True(registry.Primary.SessionEvents.IsConfigured);
        Assert.False(registry.Primary.SessionEvents.UsesKey);
        Assert.IsType<KeycloakAdminClient>(app.Services.GetRequiredService<IDirectoryAdmin>());
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
        Assert.True(options.Events.IsEnabled);
        Assert.Equal(["/my-app"], options.Admin.ManagedGroupPaths.ToArray());
    }

    [Theory]
    [InlineData("Auth:Portals:0:Host=app.example.com", "Auth:Portals:0:Host 'app.example.com' is already used")]
    [InlineData("Auth:Portals:0:ClientId=my-app", "Auth:Portals:0:ClientId 'my-app' is already used")]
    [InlineData("Auth:Events:Audience=https://auth.example.com", "Auth:Events:Audience and Auth:Events:PushServiceAccount")]
    [InlineData("Auth:Admin:Keycloak:ClientId=only-an-id", "Auth:Admin:Keycloak:ClientId and ClientSecret")]
    [InlineData("Auth:Portal:SessionEvents:Key=a-key-with-no-url", "Auth:Portal:SessionEvents:Key is set but")]
    public void A_misconfigured_optional_piece_stops_start_up_and_names_the_setting(string setting, string expected)
    {
        var args = Args(
            "Auth:Portals:0:Host=other.example.com",
            "Auth:Portals:0:ClientId=other-app",
            "Auth:Portals:0:ClientSecret=kc-secret-2",
            setting);

        // Build reads the validated options (to warn about insecure upstreams), so a bad value stops it before the host ever starts.
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Program.Build(args));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>Which portal answers, by host, once more than one is configured.</summary>
public sealed class MultiPortalEndpointTests
{
    private static Task<EndpointHost> StartAsync(Action<AuthOptions>? configure = null) =>
        EndpointHost.StartAsync(o =>
        {
            o.Portals.Add(new PortalOptions { Host = TestConfig.OtherHost, ClientId = TestConfig.OtherClientId, ClientSecret = "kc-secret-2", LoggedOutPath = "/bye", DisplayName = "Other App" });
            configure?.Invoke(o);
        });

    private static (string Name, string Value) Host(string host) => ("X-Forwarded-Host", host);

    [Fact]
    public async Task Login_on_each_portals_host_builds_that_portals_redirect_uri_from_configuration()
    {
        await using var host = await StartAsync();

        using var main = await host.SendAsync(HttpMethod.Get, "/auth/login", null, Host(TestConfig.Host));
        Assert.Equal(new Uri(TestConfig.Origin + "/auth/callback"), host.Entra.LastRedirectUri);

        using var other = await host.SendAsync(HttpMethod.Get, "/auth/login", null, Host("OTHER.example.com:443"));
        Assert.Equal(new Uri(TestConfig.OtherOrigin + "/auth/callback"), host.Entra.LastRedirectUri);
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Fact]
    public async Task Login_on_an_unknown_host_is_404_and_reaches_nobody()
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/login", null, Host("unknown.example.org"));
        using var none = await host.SendAsync(HttpMethod.Get, "/auth/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode); // no forwarded host: the server's own host name is not a portal
        Assert.Null(host.Entra.LastRedirectUri);
        Assert.Empty(host.Logins.States);
    }

    [Fact]
    public async Task A_session_is_valid_on_its_own_portal_only()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();

        using var own = await host.SendAsync(HttpMethod.Get, "/auth/validate", sid, Host(TestConfig.Host));
        using var otherPortal = await host.SendAsync(HttpMethod.Get, "/auth/validate", sid, Host(TestConfig.OtherHost));
        using var unknown = await host.SendAsync(HttpMethod.Get, "/auth/validate", sid, Host("unknown.example.org"));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherPortal.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.True(host.Sessions.Sessions.ContainsKey(SessionIds.StorageKey(sid))); // a refusal is not an end
    }

    [Fact]
    public async Task Logout_needs_the_origin_of_the_portal_it_was_sent_to_and_lands_on_that_portals_page()
    {
        await using var host = await StartAsync();

        using var wrongOrigin = await host.SendAsync(HttpMethod.Post, "/auth/logout", null, Host(TestConfig.OtherHost), ("Origin", TestConfig.Origin));
        using var otherOk = await host.SendAsync(HttpMethod.Post, "/auth/logout", null, Host(TestConfig.OtherHost), ("Origin", TestConfig.OtherOrigin));
        using var mainOk = await host.SendAsync(HttpMethod.Post, "/auth/logout", null, Host(TestConfig.Host), ("Origin", TestConfig.Origin));
        using var unknown = await host.SendAsync(HttpMethod.Post, "/auth/logout", null, Host("unknown.example.org"), ("Origin", TestConfig.Origin));

        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        Assert.Equal("/bye", otherOk.Headers.Location?.OriginalString);
        Assert.Equal("/logged-out", mainOk.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_sign_in_started_on_one_portal_cannot_be_completed_on_another()
    {
        await using var host = await StartAsync();
        host.Keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(EndpointHost.Subject, DateTimeOffset.UtcNow.AddMinutes(5), TestConfig.OtherClientId, "requester"));

        using var login = await host.SendAsync(HttpMethod.Get, "/auth/login", null, Host(TestConfig.Host));
        var binding = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/callback?code=c&state={host.Logins.States.Keys.Single()}");
        request.Headers.Add("X-Forwarded-Host", TestConfig.OtherHost);
        request.Headers.Add("Cookie", $"__Host-app_login={binding}");
        using var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Sessions.Sessions);
    }

    [Fact]
    public async Task The_no_access_page_names_the_portal_of_the_host_it_was_served_on()
    {
        await using var host = await StartAsync(o => o.Portal.DisplayName = "Main App");

        using var other = await host.SendAsync(HttpMethod.Get, "/auth/no-access", null, Host(TestConfig.OtherHost), ("Cookie", "__Host-app_noaccess=1"));
        using var main = await host.SendAsync(HttpMethod.Get, "/auth/no-access", null, Host(TestConfig.Host), ("Cookie", "__Host-app_noaccess=1"));

        Assert.Contains("You have no access to Other App", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("You have no access to Main App", await main.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

/// <summary>A project with one portal has nobody to hand off to; the hand-off must say so plainly and never guess.</summary>
public sealed class SingleportalHandoffTests
{
    [Theory]
    [InlineData("other-app")]
    [InlineData("https://elsewhere.example")]
    [InlineData("")]
    public async Task A_target_other_than_this_portal_is_an_unknown_application_and_mints_nothing(string to)
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed();

        using var response = await host.SendAsync(HttpMethod.Get, $"/auth/handoff?to={Uri.EscapeDataString(to)}&rd=/x", sid);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown application", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(host.Tickets.Tickets);
    }

    [Fact]
    public async Task Handing_off_to_this_very_portal_is_a_local_redirect()
    {
        await using var host = await EndpointHost.StartAsync();
        var sid = host.Seed();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/handoff?to=my-app&rd=%2Fitems%2F1", sid);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/items/1", response.Headers.Location?.OriginalString);
        Assert.Empty(host.Tickets.Tickets);
    }

    [Fact]
    public async Task No_forwarded_host_header_is_needed_or_read_in_a_single_portal_installation()
    {
        await using var host = await EndpointHost.StartAsync();

        using var withNone = await host.SendAsync(HttpMethod.Get, "/auth/handoff?to=my-app");
        using var withOdd = await host.SendAsync(HttpMethod.Get, "/auth/handoff?to=my-app", null, ("X-Forwarded-Host", "an.odd.host.example"));
        using var redeem = await host.SendAsync(HttpMethod.Post, "/auth/handoff/redeem", null, ("Origin", TestConfig.Origin));

        Assert.Equal(HttpStatusCode.SeeOther, withNone.StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, withOdd.StatusCode);

        // An unknown ticket falls back to the ordinary sign-in: the portal resolved, so it is not a 404.
        Assert.Equal(HttpStatusCode.SeeOther, redeem.StatusCode);
        Assert.Equal("/auth/login?rd=%2F", redeem.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_get_of_the_redeem_url_goes_home_and_the_service_has_no_second_portal_to_redeem_for()
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/handoff/redeem?ticket=anything");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }
}

/// <summary>The "No access" page: the callback's no-role outcome, the one-shot <c>/auth/no-access</c> view, and what the page may contain.</summary>
public sealed class NoAccessPageTests
{
    private const string Marker = "__Host-app_noaccess";

    [Fact]
    public async Task The_callbacks_no_role_page_names_the_app_offers_one_sign_out_button_and_keeps_the_origin_header_alive()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Portal.DisplayName = "Budget Planner");
        host.Keycloak.Next = ExchangeResult.Denied;

        using var login = await host.SendAsync(HttpMethod.Get, "/auth/login");
        var binding = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/callback?code=c&state={host.Logins.States.Keys.Single()}");
        request.Headers.Add("Cookie", $"__Host-app_login={binding}");
        using var response = await host.Http.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("<h1>You have no access to Budget Planner</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\" action=\"/auth/logout?next=sign-in\">", html, StringComparison.Ordinal);

        // Load-bearing: under the gateway's no-referrer a browser sends Origin: null on the form POST and the logout's same-origin check refuses it.
        Assert.Contains("<meta name=\"referrer\" content=\"strict-origin\">", html, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("default-src 'none'", string.Join(";", response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_without_a_configured_name_says_this_application()
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/no-access", null, ("Cookie", $"{Marker}=1"));

        Assert.Contains("<h1>You have no access to this application</h1>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_name_is_html_encoded()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Portal.DisplayName = "<script>alert(1)</script> & \"co\"");

        using var response = await host.SendAsync(HttpMethod.Get, "/auth/no-access", null, ("Cookie", $"{Marker}=1"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;co&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_renders_once_and_a_second_view_goes_home_so_a_stale_refusal_is_never_shown()
    {
        await using var host = await EndpointHost.StartAsync();

        using var first = await host.SendAsync(HttpMethod.Get, "/auth/no-access", null, ("Cookie", $"{Marker}=1"));
        using var withoutMarker = await host.SendAsync(HttpMethod.Get, "/auth/no-access");

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        var cleared = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{Marker}=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.SeeOther, withoutMarker.StatusCode);
        Assert.Equal("/", withoutMarker.Headers.Location?.OriginalString);
    }
}
