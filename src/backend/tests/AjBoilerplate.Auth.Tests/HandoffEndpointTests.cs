using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// The hand-off through the REAL endpoints, over real HTTP: Kestrel on a loopback port, the real
/// <c>/auth</c> route group and its header filter, real cookie and form parsing. Only the stores and
/// the two identity providers are fakes.
/// </summary>
/// <remarks>
/// Every log line is captured at <see cref="LogLevel.Trace"/> for EVERY category, including
/// ASP.NET Core's own, which is far more than production writes (it runs <c>Microsoft.AspNetCore</c>
/// at Warning). A ticket absent here is therefore absent from anything production could emit.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class HandoffEndpointTests : IAsyncLifetime, IDisposable
{
    private const string SourceHost = TestConfig.Host;
    private const string TargetHost = TestConfig.OtherHost;
    private const string Subject = "kc-user";
    private const string NoAccessCookie = "__Host-app_noaccess";

    private static readonly Regex TicketField = new("name=\"ticket\" value=\"([A-Za-z0-9_-]{43})\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly InMemorySessionStore _sessions = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly FakeEntra _entra = new();
    private readonly CapturingProvider _logs = new();
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        // NOT redundant with the line above, and the first run proved it: CreateBuilder loads the
        // Api's appsettings.json from the test bin, whose `Microsoft.AspNetCore: Warning` rule is
        // category-specific and so outranks a global minimum. Without these the capture held no
        // ASP.NET Core request line at all, and the absence check below would have passed over a
        // corpus missing exactly the lines that carry URLs. The positive control caught it.
        builder.Logging.AddFilter("Microsoft", LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
        builder.Logging.AddFilter("System", LogLevel.Trace);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Trace);
        builder.Logging.AddProvider(_logs);

        var options = TestConfig.TwoPortals();
        builder.Services.AddSingleton(TestConfig.Wrap(options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PortalRegistry>();
        builder.Services.AddSingleton<ISessionStore>(_sessions);
        builder.Services.AddSingleton<ILoginStateStore>(new InMemoryLoginStore());
        builder.Services.AddSingleton<IHandoffTicketStore>(new InMemoryHandoffTicketStore(TimeProvider.System));
        builder.Services.AddSingleton<IDistributedLock>(new FakeLock());
        builder.Services.AddSingleton<IEntraClient>(_entra);
        builder.Services.AddSingleton<IKeycloakExchanger>(_keycloak);
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<GoogleIdTokenVerifier>();
        builder.Services.AddSingleton(new RolesVersion(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
        builder.Services.AddSingleton(new PortalSessionId(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
        builder.Services.AddSingleton<IRevokedSessionMarks>(new InMemoryRevokedSessionMarks());
        builder.Services.AddScoped<SessionValidator>();
        builder.Services.AddScoped<SignInService>();
        builder.Services.AddScoped<ForceRefreshHandler>();
        builder.Services.AddScoped<HandoffService>();

        _app = builder.Build();
        _app.MapAuthEndpoints();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, DateTimeOffset.UtcNow.AddMinutes(30), TestConfig.OtherClientId, "requester"));
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

    private string SeedSource(DateTimeOffset? signedInAt = null)
    {
        var id = SessionIds.NewRandom();
        _sessions.Sessions[SessionIds.StorageKey(id)] = new AuthSession(
            "source-jwt", DateTimeOffset.UtcNow.AddMinutes(30), "rt-root", Subject, TestConfig.ClientId, false, signedInAt ?? DateTimeOffset.UtcNow.AddHours(-1));
        return id;
    }

    private async Task<HttpResponseMessage> StartAsync(string? sessionId, string query = "to=other-app&rd=%2Fitems%2F42")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/handoff?" + query);
        request.Headers.Add("X-Forwarded-Host", SourceHost);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"{TestConfig.SessionCookie}={sessionId}");
        }

        return await _http!.SendAsync(request);
    }

    private async Task<string> MintAsync(string sessionId)
    {
        using var response = await StartAsync(sessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return TicketField.Match(await response.Content.ReadAsStringAsync()).Groups[1].Value;
    }

    private async Task<HttpResponseMessage> RedeemAsync(string? ticket, string? origin = TestConfig.Origin, string host = TargetHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/handoff/redeem");
        request.Headers.Add("X-Forwarded-Host", host);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (ticket is not null)
        {
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("ticket", ticket)]);
        }

        return await _http!.SendAsync(request);
    }

    [Fact]
    public async Task The_source_page_posts_the_ticket_and_carries_it_in_no_url()
    {
        using var response = await StartAsync(SeedSource());
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Matches(TicketField, html);
        Assert.Contains($"action=\"https://{TargetHost}/auth/handoff/redeem\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);

        // Load-bearing: without it the browser sends `Origin: null` under the gateway's no-referrer
        // and every redeem falls back (measured, Chrome 155; docs/auth/handoff.md).
        Assert.Contains("<meta name=\"referrer\" content=\"strict-origin\">", html, StringComparison.Ordinal);

        // The CSP must allow EXACTLY the script the page carries, or the hand-off silently stalls.
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(AuthEndpoints.HandoffScript)));
        Assert.Contains($"script-src 'sha256-{hash}'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("form-action", csp, StringComparison.Ordinal);
        Assert.Contains($"<script>{AuthEndpoints.HandoffScript}</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Happy_path_end_to_end_ends_with_a_working_target_session()
    {
        var ticket = await MintAsync(SeedSource());

        using var redeem = await RedeemAsync(ticket);

        Assert.Equal(HttpStatusCode.SeeOther, redeem.StatusCode);
        Assert.Equal("/items/42", redeem.Headers.Location!.OriginalString);
        var setCookie = Assert.Single(redeem.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(TestConfig.SessionCookie + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        // The new cookie is a real session on the TARGET host, through the real validate endpoint.
        var cookie = setCookie.Split(';')[0];
        using var validate = new HttpRequestMessage(HttpMethod.Get, "/auth/validate");
        validate.Headers.Add("X-Forwarded-Host", TargetHost);
        validate.Headers.Add("Cookie", cookie);
        using var validated = await _http!.SendAsync(validate);
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.True(validated.Headers.Contains(AuthEndpoints.JwtHeader));
    }

    /// <summary>
    /// <c>/auth/login?login_hint=</c> over the real endpoint: a valid hint is forwarded, anything else
    /// is dropped and the sign-in still starts. Never a 400 -- this is the public sign-in entry.
    /// </summary>
    [Theory]
    [InlineData("user%40example.com", "user@example.com")]
    [InlineData("not-an-email", null)]
    [InlineData("a%40b.com%0D%0ASet-Cookie%3A%20x%3D1", null)]
    [InlineData("", null)]
    [InlineData("a%40b.com&login_hint=c%40d.com", null)]
    public async Task Login_forwards_a_valid_hint_and_silently_drops_the_rest(string query, string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/login?login_hint=" + query);
        request.Headers.Add("X-Forwarded-Host", SourceHost);

        using var response = await _http!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expected, _entra.LastLoginHint);
        Assert.NotNull(_entra.LastPrompt);   // control: the identity provider was reached on this call
    }

    [Fact]
    public async Task A_replayed_ticket_falls_back_to_the_ordinary_sign_in()
    {
        var ticket = await MintAsync(SeedSource());
        using var first = await RedeemAsync(ticket);

        using var replay = await RedeemAsync(ticket);

        Assert.Equal(HttpStatusCode.SeeOther, replay.StatusCode);
        Assert.Equal("/auth/login?rd=%2F", replay.Headers.Location!.OriginalString);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_cross_origin_redeem_falls_back_with_the_return_path_kept()
    {
        var ticket = await MintAsync(SeedSource());

        using var response = await RedeemAsync(ticket, origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/auth/login?rd=%2Fitems%2F42", response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_redeem_with_no_body_falls_back()
    {
        using var response = await RedeemAsync(ticket: null);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/auth/login?rd=%2F", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_redeem_on_an_unconfigured_host_is_404()
    {
        using var response = await RedeemAsync(await MintAsync(SeedSource()), host: "unknown.example.org");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_source_session_the_start_goes_to_the_targets_sign_in()
    {
        using var response = await StartAsync(sessionId: null);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"https://{TargetHost}/auth/login?rd=%2Fitems%2F42", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_source_past_its_absolute_lifetime_goes_to_the_targets_sign_in()
    {
        using var response = await StartAsync(SeedSource(signedInAt: DateTimeOffset.UtcNow.AddHours(-13)));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.StartsWith($"https://{TargetHost}/auth/login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_portal_key_is_a_404_page()
    {
        using var response = await StartAsync(SeedSource(), "to=https%3A%2F%2Fevil.example");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown application", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_external_return_path_is_not_followed()
    {
        using var start = await StartAsync(SeedSource(), "to=other-app&rd=https%3A%2F%2Fevil.example%2F");
        var ticket = TicketField.Match(await start.Content.ReadAsStringAsync()).Groups[1].Value;

        using var redeem = await RedeemAsync(ticket);

        Assert.Equal("/", redeem.Headers.Location!.OriginalString);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null, string host = TargetHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-Host", host);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await _http!.SendAsync(request);
    }

    /// <summary>
    /// The no-role outcome is NOT rendered in place on the POST URL (a refresh would re-request <c>/auth/handoff/redeem</c> with GET
    /// and show an error), and NOT a redirect to sign-in (the identity provider loop): it is a 303 to a GET page that renders.
    /// </summary>
    [Fact]
    public async Task Keycloak_no_role_answers_303_to_the_no_access_page_not_to_sign_in()
    {
        _keycloak.Next = ExchangeResult.Denied;

        using var response = await RedeemAsync(await MintAsync(SeedSource()));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(AuthEndpoints.NoAccessPath, response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());

        // Only the one-shot marker is set: no session cookie, and the marker carries no identity.
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(NoAccessCookie + "=1;", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestConfig.SessionCookie, setCookie, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> LogoutAsync(string query, string origin = "https://" + TargetHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout" + query);
        request.Headers.Add("X-Forwarded-Host", TargetHost);
        request.Headers.Add("Origin", origin);
        return await _http!.SendAsync(request);
    }

    /// <summary>
    /// The No access page's "Back to sign-in" is ONE click: it posts to <c>/auth/logout?next=sign-in</c>, which ends this portal's
    /// session and answers 303 straight to the sign-in start, not to /logged-out.
    /// </summary>
    [Fact]
    public async Task Logout_from_the_no_access_page_goes_straight_to_sign_in_asking_for_the_account()
    {
        using var response = await LogoutAsync("?next=sign-in");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/auth/login?prompt=login", response.Headers.Location!.OriginalString);
    }

    /// <summary>Ordinary sign-out is unchanged, and so is a foreign or malformed return: both land on /logged-out.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("?next=https://evil.example/")]
    [InlineData("?next=//evil.example")]
    [InlineData("?next=/auth/login%3Fprompt%3Dlogin")]
    [InlineData("?next=SIGN-IN")]
    public async Task Any_other_logout_lands_on_the_logged_out_page(string query)
    {
        using var response = await LogoutAsync(query);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/logged-out", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_cross_origin_logout_is_refused_even_with_next_sign_in()
    {
        using var response = await LogoutAsync("?next=sign-in", origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_no_access_page_renders_once_with_no_ticket_and_then_sends_a_reload_home()
    {
        _keycloak.Next = ExchangeResult.Denied;
        var ticket = await MintAsync(SeedSource());
        using var redeem = await RedeemAsync(ticket);
        var marker = redeem.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        // The browser follows the 303 with a GET, carrying the marker.
        using var page = await GetAsync(redeem.Headers.Location!.OriginalString, marker);
        var html = await page.Content.ReadAsStringAsync();

        // It names the portal of the host it was served on and offers the sign-out of that portal.
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Null(page.Headers.Location);
        Assert.Contains("<h1>You have no access to Other App</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\" action=\"/auth/logout?next=sign-in\">", html, StringComparison.Ordinal);
        Assert.Contains("Back to sign-in", html, StringComparison.Ordinal);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());

        // No ticket anywhere: not in the page, not in a field, not in the URL it was served on.
        Assert.Equal(43, ticket.Length);
        Assert.DoesNotContain(ticket, html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"ticket\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ticket", redeem.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // The view spends the marker...
        var cleared = Assert.Single(page.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(NoAccessCookie + "=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);

        // ...so a reload goes to "/", where the gateway's ordinary sign-in leads into the portal
        // once a role has been granted. Never a 405, never an error page, never a redemption.
        using var reload = await GetAsync(AuthEndpoints.NoAccessPath);
        Assert.Equal(HttpStatusCode.SeeOther, reload.StatusCode);
        Assert.Equal("/", reload.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_no_access_page_on_an_unconfigured_host_is_404()
    {
        using var response = await GetAsync(AuthEndpoints.NoAccessPath, NoAccessCookie + "=1", host: "unknown.example.org");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A GET of the redeem URL must go home, and it must not redeem: the ticket below is live and stays unspent.</summary>
    [Fact]
    public async Task A_GET_of_the_redeem_url_goes_home_and_redeems_nothing()
    {
        var ticket = await MintAsync(SeedSource());
        var sessionsBefore = _sessions.Sessions.Count;

        using var response = await GetAsync(HandoffService.RedeemPath + "?ticket=" + ticket);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(sessionsBefore, _sessions.Sessions.Count);

        // CONTROL: the ticket was not spent, so the GET did not reach RedeemAsync at all.
        using var redeem = await RedeemAsync(ticket);
        Assert.Equal(HttpStatusCode.SeeOther, redeem.StatusCode);
        Assert.Equal("/items/42", redeem.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_redeem_with_a_live_session_on_the_target_is_reused_unchanged()
    {
        var ticket = await MintAsync(SeedSource());
        using var first = await RedeemAsync(ticket);
        var cookie = first.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, HandoffService.RedeemPath);
        request.Headers.Add("X-Forwarded-Host", TargetHost);
        request.Headers.Add("Origin", TestConfig.Origin);
        request.Headers.Add("Cookie", cookie);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("ticket", await MintAsync(SeedSource()))]);
        using var reused = await _http!.SendAsync(request);

        Assert.Equal(HttpStatusCode.SeeOther, reused.StatusCode);
        Assert.Equal("/items/42", reused.Headers.Location!.OriginalString);
        Assert.False(reused.Headers.Contains("Set-Cookie"));
        Assert.Contains(_logs.Entries, e => e.Message.Contains("existing session reused: True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fault_during_redeem_falls_back_and_ends_no_session()
    {
        var sourceId = SeedSource();
        var ticket = await MintAsync(sourceId);
        _sessions.OnGet = _ => throw new InvalidOperationException("store down");

        using var response = await RedeemAsync(ticket);

        _sessions.OnGet = null;
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.StartsWith("/auth/login?rd=", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.True(_sessions.Sessions.ContainsKey(SessionIds.StorageKey(sourceId)));
        Assert.Contains(_logs.Entries, e => e.Message.Contains("hand-off redeem faulted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ticket_is_absent_from_every_captured_log_line_at_every_level()
    {
        var sourceId = SeedSource();
        var issued = new List<string>();

        var happy = await MintAsync(sourceId);
        issued.Add(happy);
        (await RedeemAsync(happy)).Dispose();
        (await RedeemAsync(happy)).Dispose();
        var foreign = await MintAsync(sourceId);
        issued.Add(foreign);
        (await RedeemAsync(foreign, origin: "https://evil.example")).Dispose();
        var faulted = await MintAsync(sourceId);
        issued.Add(faulted);
        _sessions.OnGet = _ => throw new InvalidOperationException("store down");
        (await RedeemAsync(faulted)).Dispose();
        _sessions.OnGet = null;

        var entries = _logs.Entries.ToArray();

        // CONTROLS, so the absence below is a result and not a dead capture:
        //  - ASP.NET Core's own request lines are in it, and they name the redeem path;
        //  - this service's own hand-off lines are in it;
        //  - the values searched for are real tickets, each 43 characters, all distinct.
        Assert.Contains(entries, e => e.Message.Contains("/auth/handoff/redeem", StringComparison.Ordinal) && e.Message.Contains("Request starting", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("Portal hand-off redeemed", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("origin_mismatch", StringComparison.Ordinal));
        Assert.All(issued, t => Assert.Equal(43, t.Length));
        Assert.Equal(issued.Count, issued.Distinct(StringComparer.Ordinal).Count());

        foreach (var ticket in issued)
        {
            LogAssert.Absent(entries, ticket);
            LogAssert.Absent(entries, SessionIds.StorageKey(ticket));
        }
    }
}
