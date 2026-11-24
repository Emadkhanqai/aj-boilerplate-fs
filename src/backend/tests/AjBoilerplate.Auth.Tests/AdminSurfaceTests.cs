using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>Signs Google-shaped ID tokens with the key <see cref="SigningKeys"/> publishes.</summary>
internal static class GoogleTokens
{
    public const string Audience = "https://auth.example.com";
    public const string Caller = "admin-console@my-project.iam.gserviceaccount.com";

    private static string Segment(object value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static string Signed(SigningKeys keys, string audience = Audience, string email = Caller) =>
        keys.Sign(
            Segment(new { alg = "RS256", kid = SigningKeys.Kid }),
            Segment(new
            {
                iss = "https://accounts.google.com",
                aud = audience,
                email,
                email_verified = true,
                exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            }));

    public static string Unsigned(string audience = Audience, string email = Caller) =>
        $"{Segment(new { alg = "RS256", kid = SigningKeys.Kid })}.{Segment(new { iss = "https://accounts.google.com", aud = audience, email, email_verified = true, exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds() })}";
}

/// <summary>The whole <c>/internal/admin</c> group: who may call it, and what each route answers.</summary>
public sealed class AdminSurfaceTests
{
    private const string Key = "a-long-random-admin-key";

    public static TheoryData<string, string, string?> Routes() => new()
    {
        { "GET", "/internal/admin/users?q=alice", null },
        { "POST", "/internal/admin/users", """{"email":"new@example.com","groupPath":"/my-app/editors"}""" },
        { "GET", "/internal/admin/groups", null },
        { "GET", "/internal/admin/members", null },
        { "GET", "/internal/admin/users/kc-user/groups", null },
        { "PUT", "/internal/admin/users/kc-user/groups", """{"groups":["/my-app/editors"]}""" },
        { "POST", "/internal/admin/users/kc-user/disable", null },
        { "POST", "/internal/admin/users/kc-user/enable", null },
        { "POST", "/internal/admin/users/kc-user/refresh", null },
    };

    private static async Task<HttpResponseMessage> SendAsync(
        EndpointHost host, string method, string path, string? body, string? key = Key, string? bearer = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (key is not null)
        {
            request.Headers.Add(AdminEndpoints.ApiKeyHeader, key);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await host.Http.SendAsync(request);
    }

    private static Task<EndpointHost> StartAsync(Action<Configuration.AuthOptions>? configure = null) =>
        EndpointHost.StartAsync(o =>
        {
            o.Admin.ApiKey = Key;
            o.Admin.AllowedEmailDomains.Add("example.com");
            configure?.Invoke(o);
        });

    // ---- who may call it ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task With_no_key_configured_every_route_of_the_surface_does_not_exist(string method, string path, string? body)
    {
        await using var host = await EndpointHost.StartAsync();

        using var response = await SendAsync(host, method, path, body, Key);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Directory.Calls);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_refuses_a_missing_or_wrong_key_before_anything_is_called(string method, string path, string? body)
    {
        await using var host = await StartAsync();
        var sid = host.Seed();

        using var missing = await SendAsync(host, method, path, body, key: null);
        using var wrong = await SendAsync(host, method, path, body, key: "not-the-key");
        using var near = await SendAsync(host, method, path, body, key: Key + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, near.StatusCode);
        Assert.Equal(0, host.Directory.Calls);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
        Assert.Empty(host.Sessions.Ended);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_answers_the_right_key_and_never_caches(string method, string path, string? body)
    {
        await using var host = await StartAsync();
        host.Seed();

        using var response = await SendAsync(host, method, path, body);

        Assert.True(response.IsSuccessStatusCode, $"{method} {path} answered {(int)response.StatusCode}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_google_id_token_from_an_allowed_caller_is_an_alternative_to_the_key_when_it_is_configured()
    {
        await using var host = await StartAsync(o =>
        {
            o.Admin.Audience = GoogleTokens.Audience;
            o.Admin.AllowedCallers.Add(GoogleTokens.Caller);
        });

        using var signed = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Signed(host.Google));
        using var unsigned = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Unsigned());
        using var stranger = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Signed(host.Google, email: "someone-else@my-project.iam.gserviceaccount.com"));
        using var wrongAudience = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Signed(host.Google, audience: "https://elsewhere.example"));

        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_is_ignored_while_the_google_caller_check_is_not_configured()
    {
        await using var host = await StartAsync();

        using var response = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Signed(host.Google));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Google.Fetches); // control: the key set was never even fetched
    }

    [Fact]
    public async Task With_only_the_google_check_configured_there_is_no_key_that_works()
    {
        await using var host = await EndpointHost.StartAsync(o =>
        {
            o.Admin.Audience = GoogleTokens.Audience;
            o.Admin.AllowedCallers.Add(GoogleTokens.Caller);
        });

        using var empty = await SendAsync(host, "GET", "/internal/admin/groups", null, key: string.Empty);
        using var anything = await SendAsync(host, "GET", "/internal/admin/groups", null, key: "anything");
        using var token = await SendAsync(host, "GET", "/internal/admin/groups", null, key: null, bearer: GoogleTokens.Signed(host.Google));

        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anything.StatusCode);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    }

    [Fact]
    public async Task An_oversized_or_blank_subject_is_400_on_every_subject_route_and_nothing_is_called()
    {
        await using var host = await StartAsync();
        var long300 = new string('x', 300);

        foreach (var (method, suffix, body) in new[]
        {
            ("GET", "groups", (string?)null),
            ("PUT", "groups", """{"groups":[]}"""),
            ("POST", "disable", null),
            ("POST", "enable", null),
            ("POST", "refresh", null),
        })
        {
            using var response = await SendAsync(host, method, $"/internal/admin/users/{long300}/{suffix}", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, host.Directory.Calls);
    }

    // ---- what each route answers --------------------------------------------------------------

    [Fact]
    public async Task Search_returns_the_users_the_directory_found()
    {
        await using var host = await StartAsync();

        using var response = await SendAsync(host, "GET", "/internal/admin/users?q=alice&limit=5", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user1@example.com", body.GetProperty("users")[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Members_and_groups_always_say_which_portals_the_answer_covers()
    {
        await using var host = await StartAsync();

        using var members = await SendAsync(host, "GET", "/internal/admin/members", null);
        using var groups = await SendAsync(host, "GET", "/internal/admin/groups", null);
        using var userGroups = await SendAsync(host, "GET", "/internal/admin/users/kc-user/groups", null);

        foreach (var response in new[] { members, groups, userGroups })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("portals", out _));
        }
    }

    [Fact]
    public async Task Putting_groups_changes_the_directory_and_then_flags_that_users_sessions()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();
        host.Directory.Groups.Add("/my-app/viewers");

        using var response = await SendAsync(host, "PUT", $"/internal/admin/users/{EndpointHost.Subject}/groups", """{"groups":["/my-app/editors"]}""");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/my-app/editors", body.GetProperty("added")[0].GetString());
        Assert.Equal("/my-app/viewers", body.GetProperty("removed")[0].GetString());
        Assert.Empty(body.GetProperty("withheld").EnumerateArray());
        Assert.True(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }

    [Fact]
    public async Task A_put_without_a_groups_array_is_400()
    {
        await using var host = await StartAsync();

        using var response = await SendAsync(host, "PUT", "/internal/admin/users/kc-user/groups", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Directory.Calls);
    }

    [Fact]
    public async Task Disabling_ends_the_users_sessions_as_signed_out_by_the_system_and_enabling_does_not_touch_them()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();

        using var disabled = await SendAsync(host, "POST", $"/internal/admin/users/{EndpointHost.Subject}/disable", null);

        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.False(host.Directory.Enabled);
        var ended = Assert.Single(host.Sessions.Ended);
        Assert.Equal(SessionIds.StorageKey(sid), ended.SessionKey);
        Assert.Equal(SessionEndReason.SignedOutBySystem, ended.Ending.Reason);
        Assert.Equal(SessionEndDetails.UserDisabled, ended.Ending.Detail);

        var other = host.Seed();
        using var enabled = await SendAsync(host, "POST", $"/internal/admin/users/{EndpointHost.Subject}/enable", null);

        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        Assert.True(host.Directory.Enabled);
        Assert.True(host.Sessions.Sessions.ContainsKey(SessionIds.StorageKey(other)));
    }

    [Fact]
    public async Task Creating_a_user_needs_an_email_and_a_group_and_an_allowed_domain()
    {
        await using var host = await StartAsync();

        using var noEmail = await SendAsync(host, "POST", "/internal/admin/users", """{"groupPath":"/my-app/editors"}""");
        using var noGroup = await SendAsync(host, "POST", "/internal/admin/users", """{"email":"a@example.com"}""");
        using var foreign = await SendAsync(host, "POST", "/internal/admin/users", """{"email":"a@elsewhere.org","groupPath":"/my-app/editors"}""");
        using var allowed = await SendAsync(host, "POST", "/internal/admin/users", """{"email":"a@example.com","groupPath":"/my-app/editors"}""");

        Assert.Equal(HttpStatusCode.BadRequest, noEmail.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noGroup.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, host.Directory.CreateCalls);
    }

    [Fact]
    public async Task With_no_allowed_domain_configured_no_account_is_created()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Admin.ApiKey = Key);

        using var response = await SendAsync(host, "POST", "/internal/admin/users", """{"email":"a@example.com","groupPath":"/my-app/editors"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, host.Directory.CreateCalls);
    }

    [Theory]
    [InlineData(AdminOutcome.UserNotFound, HttpStatusCode.NotFound)]
    [InlineData(AdminOutcome.GroupNotFound, HttpStatusCode.BadRequest)]
    [InlineData(AdminOutcome.Denied, HttpStatusCode.BadGateway)]
    [InlineData(AdminOutcome.UpstreamRejected, HttpStatusCode.BadGateway)]
    [InlineData(AdminOutcome.Transient, HttpStatusCode.ServiceUnavailable)]
    [InlineData(AdminOutcome.PortalNotPermitted, HttpStatusCode.Forbidden)]
    [InlineData(AdminOutcome.NotSupportedByUpstream, HttpStatusCode.NotImplemented)]
    public async Task Each_outcome_has_its_own_status_and_a_failure_never_flags_a_session(AdminOutcome outcome, HttpStatusCode expected)
    {
        await using var host = await StartAsync();
        var sid = host.Seed();
        host.Directory.Next = AdminResult.Failed(outcome);

        using var response = await SendAsync(host, "PUT", $"/internal/admin/users/{EndpointHost.Subject}/groups", """{"groups":["/my-app/editors"]}""");

        Assert.Equal(expected, response.StatusCode);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }

    [Fact]
    public async Task A_partial_failure_reports_what_actually_landed()
    {
        await using var host = await StartAsync();
        host.Directory.Next = AdminResult.PartiallyApplied(AdminOutcome.Transient, ["/my-app/editors"], ["/my-app/editors"], [], []);

        using var response = await SendAsync(host, "PUT", $"/internal/admin/users/{EndpointHost.Subject}/groups", """{"groups":["/my-app/editors"]}""");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("/my-app/editors", body.GetProperty("added")[0].GetString());
    }

    [Fact]
    public async Task The_key_the_subject_and_the_address_never_reach_a_log_line_on_the_new_routes()
    {
        await using var host = await StartAsync();
        host.Seed();

        foreach (var (method, path, body) in new[]
        {
            ("POST", "/internal/admin/users", """{"email":"private.person@example.com","groupPath":"/my-app/editors"}"""),
            ("PUT", $"/internal/admin/users/{EndpointHost.Subject}/groups", """{"groups":["/my-app/editors"]}"""),
            ("POST", $"/internal/admin/users/{EndpointHost.Subject}/disable", null),
        })
        {
            (await SendAsync(host, method, path, body)).Dispose();
            (await SendAsync(host, method, path, body, key: "wrong-key-value")).Dispose();
        }

        Assert.NotEmpty(host.Logs.Entries); // control: the logger saw these requests
        var all = string.Join("\n", host.Logs.Entries.Select(e => e.Message + string.Join(",", e.Fields.Values)));
        Assert.DoesNotContain(Key, all, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-key-value", all, StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointHost.Subject, all, StringComparison.Ordinal);
        Assert.DoesNotContain("private.person", all, StringComparison.Ordinal);
    }
}

/// <summary><c>POST /auth/events</c>: off until configured, and then only for a signed push from the configured service account.</summary>
public sealed class EventsEndpointTests
{
    private const string PushAccount = "pubsub-push@my-project.iam.gserviceaccount.com";

    private static Task<EndpointHost> StartAsync(bool configured = true) =>
        EndpointHost.StartAsync(o =>
        {
            if (configured)
            {
                o.Events.Audience = GoogleTokens.Audience;
                o.Events.PushServiceAccount = PushAccount;
            }
        });

    private static string Envelope(string json) =>
        JsonSerializer.Serialize(new { message = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), messageId = "1" }, subscription = "s" });

    private static async Task<HttpResponseMessage> PostAsync(EndpointHost host, string? bearer, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/events") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return await host.Http.SendAsync(request);
    }

    [Fact]
    public async Task It_is_off_by_default_even_for_a_perfectly_signed_push()
    {
        await using var host = await StartAsync(configured: false);
        var sid = host.Seed();

        using var response = await PostAsync(host, GoogleTokens.Signed(host.Google, email: PushAccount), Envelope($$"""{"sub":"{{EndpointHost.Subject}}"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
        Assert.Equal(0, host.Google.Fetches);
    }

    [Fact]
    public async Task It_is_off_when_only_one_of_the_two_settings_is_given()
    {
        await using var host = await EndpointHost.StartAsync(o => o.Events.Audience = GoogleTokens.Audience);

        using var response = await PostAsync(host, GoogleTokens.Signed(host.Google, email: PushAccount), Envelope("""{"sub":"x"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_push_for_a_user_flags_every_session_of_that_user_and_no_other()
    {
        await using var host = await StartAsync();
        var mine = host.Seed();
        var other = host.Seed();
        var otherKey = SessionIds.StorageKey(other);
        host.Sessions.Sessions[otherKey] = host.Sessions.Sessions[otherKey] with { Subject = "someone-else" };
        host.Sessions.Index[EndpointHost.Subject].Remove(otherKey);
        host.Sessions.Index["someone-else"] = [otherKey];

        using var response = await PostAsync(host, GoogleTokens.Signed(host.Google, email: PushAccount), Envelope($$"""{"sub":"{{EndpointHost.Subject}}"}"""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(host.Sessions.Sessions[SessionIds.StorageKey(mine)].ForceRefresh);
        Assert.False(host.Sessions.Sessions[otherKey].ForceRefresh);
    }

    [Fact]
    public async Task A_keycloak_admin_event_resource_path_names_the_user_too()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();

        using var response = await PostAsync(
            host,
            GoogleTokens.Signed(host.Google, email: PushAccount),
            Envelope($$"""{"resourcePath":"users/{{EndpointHost.Subject}}/role-mappings/clients/abc"}"""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }

    [Fact]
    public async Task A_message_that_is_not_about_a_user_or_not_json_is_acknowledged_and_flags_nothing()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();
        var token = GoogleTokens.Signed(host.Google, email: PushAccount);

        using var notAboutAUser = await PostAsync(host, token, Envelope("""{"resourcePath":"groups/abc"}"""));
        using var notJson = await PostAsync(host, token, "this is not json");

        // Acknowledged, never an error: an unparseable message is unparseable forever, and a 4xx would have it redelivered until retention ends.
        Assert.Equal(HttpStatusCode.NoContent, notAboutAUser.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, notJson.StatusCode);
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }

    [Fact]
    public async Task Everything_but_a_signed_push_from_the_configured_account_is_401_and_flags_nothing()
    {
        await using var host = await StartAsync();
        var sid = host.Seed();
        var body = Envelope($$"""{"sub":"{{EndpointHost.Subject}}"}""");

        using var none = await PostAsync(host, null, body);
        using var unsigned = await PostAsync(host, GoogleTokens.Unsigned(email: PushAccount), body);
        using var stranger = await PostAsync(host, GoogleTokens.Signed(host.Google, email: "someone-else@my-project.iam.gserviceaccount.com"), body);
        using var wrongAudience = await PostAsync(host, GoogleTokens.Signed(host.Google, audience: "https://elsewhere.example", email: PushAccount), body);

        Assert.All([none, unsigned, stranger, wrongAudience], r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.False(host.Sessions.Sessions[SessionIds.StorageKey(sid)].ForceRefresh);
    }
}
