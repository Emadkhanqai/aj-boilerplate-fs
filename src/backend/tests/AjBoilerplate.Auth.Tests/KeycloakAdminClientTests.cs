using System.Net;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using Microsoft.Extensions.Logging;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// A small in-memory Keycloak: the realm's users and groups behind the Admin REST API's real paths and status codes, plus a request
/// log, so the client is tested against request SHAPES (method, path, body) and answers, not against its own assumptions.
/// </summary>
internal sealed class FakeKeycloakServer : HttpMessageHandler
{
    public const string Realm = "default";

    public sealed record Group(string Id, string Name, string Path);

    public Dictionary<string, Group> Groups { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement> Users { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, HashSet<string>> Membership { get; } = new(StringComparer.Ordinal);

    public List<(HttpMethod Method, string PathAndQuery, string Body, string? Authorization)> Requests { get; } = [];

    public int TokenRequests => Requests.Count(r => r.PathAndQuery.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal));

    public int AccessTokenSerial { get; private set; }

    public int ExpiresIn { get; set; } = 300;

    /// <summary>When set, answers this for the next N admin (non-token) calls.</summary>
    public (int Status, int Times)? FailAdminCalls { get; set; }

    /// <summary>When set, the admin call with this method and path prefix answers the given status.</summary>
    public Func<HttpMethod, string, int?>? Script { get; set; }

    public bool TokenRefused { get; set; }

    public Exception? Throw { get; set; }

    /// <summary>The bearer value currently accepted by the admin API; a stale one is answered 401.</summary>
    public string? AcceptedToken { get; set; }

    public FakeKeycloakServer AddGroup(string path)
    {
        var id = "g-" + (Groups.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Groups[id] = new Group(id, path[(path.LastIndexOf('/') + 1)..], path);
        return this;
    }

    public FakeKeycloakServer AddUser(string id, string email, bool enabled = true, params string[] groupPaths)
    {
        Users[id] = JsonSerializer.SerializeToElement(new { id, username = email, email, firstName = "F", lastName = "L", enabled, attributes = new { keep = new[] { "me" } } });
        Membership[id] = [.. groupPaths];
        return this;
    }

    public IEnumerable<(HttpMethod Method, string PathAndQuery, string Body, string? Authorization)> Writes =>
        Requests.Where(r => r.Method != HttpMethod.Get && !r.PathAndQuery.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        Requests.Add((request.Method, pathAndQuery, body, request.Headers.Authorization?.ToString()));

        if (Throw is not null)
        {
            throw Throw;
        }

        if (pathAndQuery.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
        {
            if (TokenRefused)
            {
                return Reply(401, new { error = "unauthorized_client" });
            }

            AccessTokenSerial++;
            AcceptedToken = "access-token-" + AccessTokenSerial.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Reply(200, new { access_token = AcceptedToken, expires_in = ExpiresIn, token_type = "Bearer" });
        }

        if (request.Headers.Authorization?.Parameter != AcceptedToken)
        {
            return Reply(401, new { error = "HTTP 401 Unauthorized" });
        }

        if (FailAdminCalls is { Times: > 0 } failing)
        {
            FailAdminCalls = (failing.Status, failing.Times - 1);
            return Reply(failing.Status, new { error = "scripted" });
        }

        if (Script?.Invoke(request.Method, pathAndQuery) is { } scripted)
        {
            return Reply(scripted, new { error = "scripted" });
        }

        return Route(request.Method, request.RequestUri, body);
    }

    private HttpResponseMessage Route(HttpMethod method, Uri uri, string body)
    {
        var prefix = $"/admin/realms/{Realm}/";
        var path = uri.AbsolutePath[(uri.AbsolutePath.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var parts = path.Split('/');

        if (method == HttpMethod.Get && parts[0] == "group-by-path")
        {
            var wanted = "/" + string.Join('/', parts.Skip(1).Select(Uri.UnescapeDataString));
            var group = Groups.Values.FirstOrDefault(g => g.Path == wanted);
            return group is null ? Reply(404, new { error = "Group path does not exist" }) : Reply(200, new { id = group.Id, name = group.Name, path = group.Path });
        }

        if (method == HttpMethod.Get && parts[0] == "groups" && parts.Length == 3 && parts[2] == "children")
        {
            var parent = Groups[parts[1]].Path;
            var children = Groups.Values.Where(g => g.Path.StartsWith(parent + "/", StringComparison.Ordinal) && g.Path.LastIndexOf('/') == parent.Length)
                .Select(g => new { id = g.Id, name = g.Name, path = g.Path });
            return Reply(200, children);
        }

        if (method == HttpMethod.Get && parts[0] == "groups" && parts.Length == 3 && parts[2] == "members")
        {
            var path2 = Groups[parts[1]].Path;
            return Reply(200, Users.Where(u => Membership[u.Key].Contains(path2)).Select(u => u.Value));
        }

        if (parts[0] == "users" && parts.Length == 1)
        {
            if (method == HttpMethod.Get)
            {
                var email = query["email"];
                var search = query["search"];
                var found = Users.Values.Where(u =>
                    (email is null || string.Equals(u.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
                    && (search is null || u.GetProperty("email").GetString()!.Contains(search, StringComparison.OrdinalIgnoreCase)));
                return Reply(200, found);
            }

            if (method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                var email = doc.RootElement.GetProperty("email").GetString()!;
                if (Users.Values.Any(u => u.GetProperty("email").GetString() == email))
                {
                    return Reply(409, new { errorMessage = "User exists with same email" });
                }

                var id = "u-new-" + (Users.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Users[id] = JsonSerializer.SerializeToElement(new { id, username = email, email, enabled = true });
                Membership[id] = [];
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri($"https://keycloak.example/admin/realms/{Realm}/users/{id}");
                return created;
            }
        }

        if (parts[0] == "users" && parts.Length >= 2)
        {
            var id = Uri.UnescapeDataString(parts[1]);
            if (!Users.TryGetValue(id, out var user))
            {
                return Reply(404, new { error = "User not found" });
            }

            if (parts.Length == 2 && method == HttpMethod.Get)
            {
                return Reply(200, user);
            }

            if (parts.Length == 2 && method == HttpMethod.Put)
            {
                Users[id] = JsonDocument.Parse(body).RootElement.Clone();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (parts.Length == 3 && parts[2] == "groups" && method == HttpMethod.Get)
            {
                return Reply(200, Membership[id].Select(p => Groups.Values.First(g => g.Path == p)).Select(g => new { id = g.Id, name = g.Name, path = g.Path }));
            }

            if (parts.Length == 4 && parts[2] == "groups")
            {
                var group = Groups[parts[3]];
                if (method == HttpMethod.Put)
                {
                    Membership[id].Add(group.Path);
                }
                else if (method == HttpMethod.Delete)
                {
                    Membership[id].Remove(group.Path);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        return Reply(404, new { error = "no route in the fake: " + method + " " + path });
    }

    private static HttpResponseMessage Reply(int status, object body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

public sealed class KeycloakAdminClientTests
{
    private const string Seats = "/my-app";
    private const string Editors = "/my-app/editors";
    private const string Viewers = "/my-app/viewers";
    private const string Unmanaged = "/other/admins";
    private const string ClientSecret = "admin-client-secret-value";

    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeKeycloakServer _server = new();
    private readonly CapturingLogger<KeycloakAdminClient> _log = new();
    private readonly AuthOptions _options = TestConfig.Options();

    public KeycloakAdminClientTests()
    {
        _options.Keycloak.BaseUrl = "https://keycloak.example";
        _options.Admin.Keycloak.ClientId = "directory-admin";
        _options.Admin.Keycloak.ClientSecret = ClientSecret;
        _options.Admin.ManagedGroupPaths.Add(Seats);
        _server.AddGroup(Seats).AddGroup(Editors).AddGroup(Viewers).AddGroup("/other").AddGroup(Unmanaged);
    }

    private KeycloakAdminClient Sut() => new(new Factory(_server), TestConfig.Wrap(_options), _time, _log);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task The_service_account_signs_in_with_client_credentials_and_the_token_is_cached_until_it_nears_expiry()
    {
        _server.AddUser("u1", "a@example.com", true, Editors);
        var sut = Sut();

        await sut.GetGroupsAsync("u1", default);
        await sut.GetGroupsAsync("u1", default);

        var token = _server.Requests.First(r => r.PathAndQuery.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("/realms/default/protocol/openid-connect/token", token.PathAndQuery);
        Assert.Contains("grant_type=client_credentials", token.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=directory-admin", token.Body, StringComparison.Ordinal);
        Assert.Equal(1, _server.TokenRequests);
        Assert.All(_server.Requests.Where(r => !r.PathAndQuery.Contains("openid-connect", StringComparison.Ordinal)), r => Assert.Equal("Bearer access-token-1", r.Authorization));

        // 300 s lifetime less the 30 s skew: inside it the cached token serves, past it a new one is minted.
        _time.Now += TimeSpan.FromSeconds(200);
        await sut.GetGroupsAsync("u1", default);
        Assert.Equal(1, _server.TokenRequests);

        _time.Now += TimeSpan.FromSeconds(100);
        await sut.GetGroupsAsync("u1", default);
        Assert.Equal(2, _server.TokenRequests);
        Assert.Equal("Bearer access-token-2", _server.Requests[^1].Authorization);
    }

    [Fact]
    public async Task A_token_the_server_no_longer_accepts_is_replaced_once_and_the_call_retried()
    {
        _server.AddUser("u1", "a@example.com", true, Editors);
        var sut = Sut();
        await sut.GetGroupsAsync("u1", default);
        _server.AcceptedToken = "rotated-elsewhere";

        var result = await sut.GetGroupsAsync("u1", default);

        Assert.True(result.Succeeded);
        Assert.Equal(2, _server.TokenRequests);
    }

    [Fact]
    public async Task A_credential_the_server_refuses_is_denied_and_logged_with_the_upstream_status()
    {
        _server.TokenRefused = true;

        var result = await Sut().GetGroupsAsync("u1", default);

        Assert.Equal(AdminOutcome.Denied, result.Outcome);
        var line = Assert.Single(_log.Entries);
        Assert.Equal(401, line.Fields["UpstreamStatus"]);
        Assert.Equal("POST /realms/{realm}/protocol/openid-connect/token", line.Field("Operation"));
    }

    [Theory]
    [InlineData(401, AdminOutcome.Denied)]
    [InlineData(403, AdminOutcome.Denied)]
    [InlineData(404, AdminOutcome.UserNotFound)]
    [InlineData(400, AdminOutcome.UpstreamRejected)]
    [InlineData(409, AdminOutcome.UpstreamRejected)]
    [InlineData(422, AdminOutcome.UpstreamRejected)]
    [InlineData(408, AdminOutcome.Transient)]
    [InlineData(429, AdminOutcome.Transient)]
    [InlineData(500, AdminOutcome.Transient)]
    [InlineData(503, AdminOutcome.Transient)]
    public void Every_upstream_status_has_one_outcome(int status, AdminOutcome expected) =>
        Assert.Equal(expected, KeycloakAdminClient.Classify((HttpStatusCode)status, AdminOutcome.UserNotFound));

    [Fact]
    public async Task A_403_from_the_admin_api_is_denied_and_the_log_keeps_the_status_apart_from_the_outcome()
    {
        _server.AddUser("u1", "a@example.com");
        _server.Script = (_, path) => path.Contains("/users/", StringComparison.Ordinal) ? 403 : null;

        var result = await Sut().GetGroupsAsync("u1", default);

        Assert.Equal(AdminOutcome.Denied, result.Outcome);
        var line = Assert.Single(_log.Entries);
        Assert.Equal(403, line.Fields["UpstreamStatus"]);
        Assert.Equal(AdminOutcome.Denied, line.Fields["Outcome"]);
        Assert.Equal("GET /users/{id}/groups", line.Field("Operation"));
    }

    [Fact]
    public async Task A_transport_failure_is_transient()
    {
        _server.Throw = new HttpRequestException("connection refused");

        Assert.Equal(AdminOutcome.Transient, (await Sut().GetGroupsAsync("u1", default)).Outcome);
    }

    [Fact]
    public async Task The_group_tree_lists_each_managed_path_with_its_seats_and_says_which_could_not_be_read()
    {
        _options.Admin.ManagedGroupPaths.Add("/not-in-the-realm");

        var view = await Sut().GetGroupTreeAsync(default);

        Assert.Equal(AdminOutcome.Success, view.Outcome);
        Assert.Equal([Seats, Editors, Viewers], view.Groups.Select(g => g.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Seats, view.Groups.Single(g => g.Path == Editors).ParentPath);
        Assert.Equal(AdminOutcome.Success, view.Portals.Single(p => p.Path == Seats).Outcome);
        Assert.Equal(AdminOutcome.GroupNotFound, view.Portals.Single(p => p.Path == "/not-in-the-realm").Outcome);
        Assert.DoesNotContain(view.Groups, g => g.Path == Unmanaged);
    }

    [Fact]
    public async Task Members_are_each_person_once_with_every_seat_they_hold()
    {
        _server.AddUser("u1", "a@example.com", true, Editors, Viewers).AddUser("u2", "b@example.com", true, Viewers).AddUser("u3", "nobody@example.com", true, Unmanaged);

        var view = await Sut().GetPortalMembersAsync(default);

        Assert.Equal(AdminOutcome.Success, view.Outcome);
        Assert.Equal(["a@example.com", "b@example.com"], view.Members.Select(m => m.User.Username).ToArray());
        Assert.Equal([Editors, Viewers], view.Members[0].Groups.ToArray());
        Assert.Equal([Viewers], view.Members[1].Groups.ToArray());
    }

    [Fact]
    public async Task An_unreadable_managed_path_contributes_no_members_and_a_total_outage_is_not_an_empty_list()
    {
        _server.AddUser("u1", "a@example.com", true, Editors);
        _server.Script = (_, path) => path.Contains("/members", StringComparison.Ordinal) ? 500 : null;

        var view = await Sut().GetPortalMembersAsync(default);

        Assert.Equal(AdminOutcome.Transient, view.Outcome);
        Assert.Empty(view.Members);
        Assert.Equal(AdminOutcome.Transient, Assert.Single(view.Portals).Outcome);
    }

    [Fact]
    public async Task A_user_reads_back_only_the_seats_under_a_managed_path()
    {
        _server.AddUser("u1", "a@example.com", true, Editors, Unmanaged);

        var result = await Sut().GetGroupsAsync("u1", default);

        Assert.Equal([Editors], result.Groups!.ToArray());
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        Assert.Equal(AdminOutcome.UserNotFound, (await Sut().GetGroupsAsync("nobody", default)).Outcome);
        Assert.Equal(AdminOutcome.UserNotFound, (await Sut().SetGroupsAsync("nobody", [Editors], default)).Outcome);
        Assert.Equal(AdminOutcome.UserNotFound, (await Sut().SetEnabledAsync("nobody", false, default)).Outcome);
    }

    [Fact]
    public async Task Set_groups_adds_and_removes_only_managed_seats_and_leaves_every_other_group_alone()
    {
        _server.AddUser("u1", "a@example.com", true, Viewers, Unmanaged);

        var result = await Sut().SetGroupsAsync("u1", [Editors], default);

        Assert.True(result.Succeeded);
        Assert.Equal([Editors], result.Groups!.ToArray());
        Assert.Equal([Editors], result.Added!.ToArray());
        Assert.Equal([Viewers], result.Removed!.ToArray());
        Assert.Equal([Editors, Unmanaged], _server.Membership["u1"].Order(StringComparer.Ordinal).ToArray());

        var writes = _server.Writes.Select(w => $"{w.Method} {w.PathAndQuery}").ToArray();
        Assert.Equal(
            [$"PUT /admin/realms/default/users/u1/groups/g-2", $"DELETE /admin/realms/default/users/u1/groups/g-3"],
            writes);
    }

    [Fact]
    public async Task Repeating_the_same_declaration_changes_nothing_and_writes_nothing()
    {
        _server.AddUser("u1", "a@example.com", true, Editors);

        var result = await Sut().SetGroupsAsync("u1", [Editors], default);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Added!);
        Assert.Empty(result.Removed!);
        Assert.Empty(_server.Writes);
    }

    [Theory]
    [InlineData("/other/admins")]
    [InlineData("/my-app")]
    [InlineData("/my-appx/editors")]
    [InlineData("/")]
    [InlineData("")]
    public async Task A_path_that_is_not_a_seat_under_a_managed_path_is_refused_before_anything_is_called(string path)
    {
        _server.AddUser("u1", "a@example.com", true, Editors);

        var result = await Sut().SetGroupsAsync("u1", [Editors, path], default);

        Assert.Equal(AdminOutcome.GroupNotFound, result.Outcome);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_seat_that_does_not_exist_in_the_realm_changes_nothing()
    {
        _server.AddUser("u1", "a@example.com", true, Viewers);

        var result = await Sut().SetGroupsAsync("u1", [Editors, "/my-app/ghosts"], default);

        Assert.Equal(AdminOutcome.GroupNotFound, result.Outcome);
        Assert.Empty(_server.Writes);
        Assert.Equal([Viewers], _server.Membership["u1"].ToArray());
    }

    [Fact]
    public async Task A_failure_half_way_reports_what_actually_landed()
    {
        _server.AddUser("u1", "a@example.com", true, Viewers);
        _server.Script = (method, path) => method == HttpMethod.Delete ? 503 : null;

        var result = await Sut().SetGroupsAsync("u1", [Editors], default);

        Assert.Equal(AdminOutcome.Transient, result.Outcome);
        Assert.Equal([Editors], result.Added!.ToArray());
        Assert.Empty(result.Removed!);
        Assert.Equal([Editors, Viewers], result.Groups!.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Creating_a_user_makes_the_account_then_the_seat()
    {
        var result = await Sut().CreateUserAsync(new NewUser(" New@Example.com ", "Nina", "New", Editors), default);

        Assert.True(result.Succeeded);
        Assert.True(result.Created);
        Assert.Equal("u-new-1", result.User!.Subject);
        Assert.Equal([Editors], result.Added!.ToArray());

        var create = Assert.Single(_server.Writes, w => w.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(create.Body);
        Assert.Equal("New@Example.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("New@Example.com", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("Nina", body.RootElement.GetProperty("firstName").GetString());
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Contains(Editors, _server.Membership["u-new-1"]);
    }

    [Fact]
    public async Task An_existing_account_is_attached_not_duplicated_and_a_seat_already_held_is_a_no_op()
    {
        _server.AddUser("u1", "a@example.com", true, Editors);

        var result = await Sut().CreateUserAsync(new NewUser("A@example.com", "A", "A", Editors), default);

        Assert.True(result.Succeeded);
        Assert.False(result.Created);
        Assert.Equal("u1", result.User!.Subject);
        Assert.Empty(result.Added!);
        Assert.DoesNotContain(_server.Writes, w => w.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Two_accounts_for_one_address_are_for_a_person_to_resolve_and_nothing_is_granted()
    {
        _server.AddUser("u1", "dup@example.com").AddUser("u2", "dup@example.com");

        var result = await Sut().CreateUserAsync(new NewUser("dup@example.com", "D", "D", Editors), default);

        Assert.Equal(AdminOutcome.UpstreamRejected, result.Outcome);
        Assert.Empty(_server.Writes);
    }

    [Fact]
    public async Task A_conflict_on_create_is_rejected_not_retried()
    {
        _server.Script = (method, path) => method == HttpMethod.Post && path.EndsWith("/users", StringComparison.Ordinal) ? 409 : null;

        var result = await Sut().CreateUserAsync(new NewUser("x@example.com", "X", "X", Editors), default);

        Assert.Equal(AdminOutcome.UpstreamRejected, result.Outcome);
    }

    [Fact]
    public async Task Creating_in_a_path_outside_the_managed_ones_is_refused_before_anything_is_called()
    {
        var result = await Sut().CreateUserAsync(new NewUser("x@example.com", "X", "X", Unmanaged), default);

        Assert.Equal(AdminOutcome.GroupNotFound, result.Outcome);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Disabling_sends_the_whole_representation_back_with_only_enabled_changed()
    {
        _server.AddUser("u1", "a@example.com");

        var result = await Sut().SetEnabledAsync("u1", false, default);

        Assert.True(result.Succeeded);
        Assert.False(result.User!.Enabled);
        var put = Assert.Single(_server.Writes);
        Assert.Equal("PUT /admin/realms/default/users/u1", $"{put.Method} {put.PathAndQuery}");
        using var body = JsonDocument.Parse(put.Body);
        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("a@example.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("me", body.RootElement.GetProperty("attributes").GetProperty("keep")[0].GetString());
    }

    [Fact]
    public async Task Search_asks_keycloak_only_for_a_real_term()
    {
        _server.AddUser("u1", "alice@example.com").AddUser("u2", "bob@example.com");
        var sut = Sut();

        var blank = await sut.SearchUsersAsync("   ", 25, default);
        Assert.Empty(blank.Users);
        Assert.Empty(_server.Requests);

        var found = await sut.SearchUsersAsync("alice", 25, default);
        Assert.Equal(AdminOutcome.Success, found.Outcome);
        Assert.Equal("alice@example.com", Assert.Single(found.Users).Email);
        Assert.Contains("search=alice", _server.Requests[^1].PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("max=25", _server.Requests[^1].PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_secret_token_or_address_reaches_a_log_line()
    {
        _server.AddUser("u1", "private.person@example.com");
        _server.Script = (_, path) => path.Contains("/users", StringComparison.Ordinal) ? 500 : null;
        var sut = Sut();

        await sut.SetEnabledAsync("u1", false, default);
        await sut.CreateUserAsync(new NewUser("private.person@example.com", "P", "P", Editors), default);

        Assert.NotEmpty(_log.Entries); // control: the logger saw these calls
        var all = string.Join("\n", _log.Entries.Select(e => e.Message + " " + string.Join(",", e.Fields.Values)));
        Assert.DoesNotContain(ClientSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token", all, StringComparison.Ordinal);
        Assert.DoesNotContain("private.person", all, StringComparison.Ordinal);
        Assert.DoesNotContain("u1", all.Replace("users/u1", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.All(_log.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
    }

    [Fact]
    public async Task With_no_admin_client_configured_every_operation_says_not_supported_rather_than_answering_empty()
    {
        IDirectoryAdmin none = new NotConfiguredDirectoryAdmin();

        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.GetGroupTreeAsync(default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.GetPortalMembersAsync(default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.SearchUsersAsync("x", 1, default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.GetGroupsAsync("u", default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.SetGroupsAsync("u", [], default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.CreateUserAsync(new NewUser("a@b.co", "a", "b", Editors), default)).Outcome);
        Assert.Equal(AdminOutcome.NotSupportedByUpstream, (await none.SetEnabledAsync("u", true, default)).Outcome);
    }
}
