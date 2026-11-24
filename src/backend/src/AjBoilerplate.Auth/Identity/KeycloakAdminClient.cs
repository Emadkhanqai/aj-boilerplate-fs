using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Identity;

/// <summary>
/// <see cref="IDirectoryAdmin"/> over the Keycloak Admin REST API, authenticated as a dedicated client's service account
/// (client-credentials), never as this app's own client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only groups UNDER a managed path are ever read or written</b> (<c>Auth:Admin:ManagedGroupPaths</c>): a requested path that is not
/// a seat under one of them is refused before any write, so a caller cannot use this surface to put somebody in a realm group that
/// has nothing to do with this app.
/// </para>
/// <para>
/// <b>The classifier keeps the cause distinct.</b> Every call is logged by its OPERATION name and the upstream status, separately from
/// the <see cref="AdminOutcome"/> it folds into: a 401 (our credential is wrong) and a 403 (the service account lacks a realm-management
/// role) both read as <see cref="AdminOutcome.Denied"/> to the caller, and only the log can tell them apart. No token, secret, user
/// identifier, e-mail address or response body is ever logged.
/// </para>
/// <para>
/// Needs Keycloak 23 or later (<c>GET /groups/{id}/children</c>). The service account needs the <c>realm-management</c> roles
/// <c>view-users</c>, <c>query-users</c>, <c>query-groups</c> and <c>manage-users</c>.
/// </para>
/// </remarks>
internal sealed partial class KeycloakAdminClient : IDirectoryAdmin
{
    public const string HttpClientName = "keycloak-admin";

    private const int PageSize = 100;
    private const int MaxPages = 50;
    private const int MaxDepth = 4;
    private static readonly TimeSpan TokenSkew = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly AuthOptions _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<KeycloakAdminClient> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenUntil;

    public KeycloakAdminClient(IHttpClientFactory http, IOptions<AuthOptions> options, TimeProvider time, ILogger<KeycloakAdminClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _auth = options.Value;
        _time = time;
        _logger = logger;
    }

    private AdminOptions Admin => _auth.Admin;

    private string BaseUrl => (string.IsNullOrWhiteSpace(Admin.Keycloak.BaseUrl) ? _auth.Keycloak.BaseUrl : Admin.Keycloak.BaseUrl).TrimEnd('/');

    private string Realm => Uri.EscapeDataString(string.IsNullOrWhiteSpace(Admin.Keycloak.Realm) ? _auth.Keycloak.Realm : Admin.Keycloak.Realm);

    private string AdminRoot => $"{BaseUrl}/admin/realms/{Realm}";

    public async Task<(AdminOutcome Outcome, IReadOnlyList<RealmUser> Users)> SearchUsersAsync(string term, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return (AdminOutcome.Success, []);
        }

        try
        {
            var max = Math.Clamp(limit, 1, PageSize);
            var json = await GetAsync(Op.SearchUsers, $"{AdminRoot}/users?search={Uri.EscapeDataString(term.Trim())}&first=0&max={max.ToString(CultureInfo.InvariantCulture)}", AdminOutcome.UserNotFound, cancellationToken);
            return (AdminOutcome.Success, [.. json.EnumerateArray().Select(ToUser)]);
        }
        catch (DirectoryFailure failure)
        {
            return (failure.Outcome, []);
        }
    }

    public async Task<GroupTreeView> GetGroupTreeAsync(CancellationToken cancellationToken)
    {
        var groups = new List<RealmGroup>();
        var portals = new List<PortalAccess>();
        foreach (var managed in Admin.ManagedGroupPaths)
        {
            try
            {
                var root = await FindGroupAsync(managed, cancellationToken);
                groups.Add(new RealmGroup(managed, root.Name, ParentOf(managed)));
                foreach (var seat in await SeatsAsync(root, cancellationToken))
                {
                    groups.Add(new RealmGroup(seat.Path, seat.Name, ParentOf(seat.Path)));
                }

                portals.Add(new PortalAccess(managed, AdminOutcome.Success));
            }
            catch (DirectoryFailure failure)
            {
                portals.Add(new PortalAccess(managed, failure.Outcome));
            }
        }

        return new GroupTreeView(Summary(portals), groups, portals);
    }

    public async Task<MembersView> GetPortalMembersAsync(CancellationToken cancellationToken)
    {
        var members = new Dictionary<string, (RealmUser User, List<string> Seats)>(StringComparer.Ordinal);
        var portals = new List<PortalAccess>();
        foreach (var managed in Admin.ManagedGroupPaths)
        {
            try
            {
                var root = await FindGroupAsync(managed, cancellationToken);
                var seats = await SeatsAsync(root, cancellationToken);

                // Read every seat of this portal BEFORE recording any of it, so a portal whose read fails half-way is reported as
                // unreadable and contributes no members, never a partial list that reads as complete.
                var read = new List<(GroupRef Seat, List<RealmUser> Users)>();
                foreach (var seat in seats)
                {
                    read.Add((seat, await MembersOfAsync(seat, cancellationToken)));
                }

                foreach (var (seat, users) in read)
                {
                    foreach (var user in users)
                    {
                        if (!members.TryGetValue(user.Subject, out var entry))
                        {
                            entry = (user, []);
                            members[user.Subject] = entry;
                        }

                        entry.Seats.Add(seat.Path);
                    }
                }

                portals.Add(new PortalAccess(managed, AdminOutcome.Success));
            }
            catch (DirectoryFailure failure)
            {
                portals.Add(new PortalAccess(managed, failure.Outcome));
            }
        }

        return new MembersView(
            Summary(portals),
            [.. members.Values.Select(m => new PortalMember(m.User, [.. m.Seats.Order(StringComparer.Ordinal)])).OrderBy(m => m.User.Username, StringComparer.OrdinalIgnoreCase)],
            portals);
    }

    public async Task<AdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken)
    {
        try
        {
            var current = await CurrentManagedGroupsAsync(subject, cancellationToken);
            return new AdminResult(AdminOutcome.Success, Groups: [.. current.Select(g => g.Path).Order(StringComparer.Ordinal)], Portals: AllReadable());
        }
        catch (DirectoryFailure failure)
        {
            return AdminResult.Failed(failure.Outcome);
        }
    }

    public async Task<AdminResult> SetGroupsAsync(string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupPaths);

        // Refused as a whole, before anything is read or written: one path that is not a seat means the request is not understood.
        var wanted = groupPaths.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(p => !IsSeat(p)))
        {
            return AdminResult.GroupNotFound;
        }

        List<GroupRef> current;
        try
        {
            current = await CurrentManagedGroupsAsync(subject, cancellationToken);
        }
        catch (DirectoryFailure failure)
        {
            return AdminResult.Failed(failure.Outcome);
        }

        var held = current.Select(g => g.Path).ToHashSet(StringComparer.Ordinal);
        var toAdd = wanted.Where(p => !held.Contains(p)).Order(StringComparer.Ordinal).ToList();
        var toRemove = current.Where(g => !wanted.Contains(g.Path, StringComparer.Ordinal)).OrderBy(g => g.Path, StringComparer.Ordinal).ToList();

        // Resolve every group to add BEFORE the first write, so an unknown path changes nothing.
        var adds = new List<GroupRef>();
        try
        {
            foreach (var path in toAdd)
            {
                adds.Add(await FindGroupAsync(path, cancellationToken));
            }
        }
        catch (DirectoryFailure failure)
        {
            return AdminResult.Failed(failure.Outcome == AdminOutcome.UserNotFound ? AdminOutcome.GroupNotFound : failure.Outcome);
        }

        var resulting = new HashSet<string>(held, StringComparer.Ordinal);
        var added = new List<string>();
        var removed = new List<string>();
        try
        {
            foreach (var group in adds)
            {
                await SendAsync(Op.AddMembership, HttpMethod.Put, $"{AdminRoot}/users/{Uri.EscapeDataString(subject)}/groups/{Uri.EscapeDataString(group.Id)}", null, AdminOutcome.UserNotFound, cancellationToken);
                resulting.Add(group.Path);
                added.Add(group.Path);
            }

            foreach (var group in toRemove)
            {
                await SendAsync(Op.RemoveMembership, HttpMethod.Delete, $"{AdminRoot}/users/{Uri.EscapeDataString(subject)}/groups/{Uri.EscapeDataString(group.Id)}", null, AdminOutcome.UserNotFound, cancellationToken);
                resulting.Remove(group.Path);
                removed.Add(group.Path);
            }
        }
        catch (DirectoryFailure failure)
        {
            return added.Count + removed.Count == 0
                ? AdminResult.Failed(failure.Outcome)
                : AdminResult.PartiallyApplied(failure.Outcome, [.. resulting.Order(StringComparer.Ordinal)], added, removed, []);
        }

        return new AdminResult(
            AdminOutcome.Success,
            Groups: [.. resulting.Order(StringComparer.Ordinal)],
            Added: added,
            Removed: removed,
            Portals: AllReadable(),
            Withheld: []);
    }

    public async Task<AdminResult> CreateUserAsync(NewUser request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = Normalize(request.GroupPath);
        if (!IsSeat(path))
        {
            return AdminResult.GroupNotFound;
        }

        var email = request.Email.Trim();
        try
        {
            var group = await FindGroupAsync(path, cancellationToken);

            var existing = (await GetAsync(
                Op.FindUserByEmail,
                $"{AdminRoot}/users?email={Uri.EscapeDataString(email)}&exact=true&first=0&max=2",
                AdminOutcome.UserNotFound,
                cancellationToken)).EnumerateArray().ToList();

            // More than one account for one e-mail is for a person to resolve: guessing which one a seat belongs to is a grant to
            // the wrong person.
            if (existing.Count > 1)
            {
                LogRefused(_logger, Op.FindUserByEmail, 200, AdminOutcome.UpstreamRejected, "ambiguous_email");
                return AdminResult.Failed(AdminOutcome.UpstreamRejected);
            }

            bool created;
            RealmUser user;
            if (existing.Count == 1)
            {
                created = false;
                user = ToUser(existing[0]);
            }
            else
            {
                created = true;
                var reply = await SendAsync(
                    Op.CreateUser,
                    HttpMethod.Post,
                    $"{AdminRoot}/users",
                    new { username = email, email, firstName = request.FirstName, lastName = request.LastName, enabled = true },
                    AdminOutcome.UserNotFound,
                    cancellationToken);
                var id = reply.Location is { } location ? location.Segments[^1].Trim('/') : null;
                if (string.IsNullOrEmpty(id))
                {
                    LogRefused(_logger, Op.CreateUser, (int)reply.Status, AdminOutcome.Transient, "no_location");
                    return AdminResult.Failed(AdminOutcome.Transient);
                }

                user = new RealmUser(id, email, email, request.FirstName, request.LastName, Enabled: true);
            }

            var held = created
                ? []
                : (await CurrentManagedGroupsAsync(user.Subject, cancellationToken)).Select(g => g.Path).ToHashSet(StringComparer.Ordinal);
            var already = held.Contains(path);

            // PUT is idempotent in Keycloak: a retry after a timeout grants the same seat rather than a second one.
            await SendAsync(Op.AddMembership, HttpMethod.Put, $"{AdminRoot}/users/{Uri.EscapeDataString(user.Subject)}/groups/{Uri.EscapeDataString(group.Id)}", null, AdminOutcome.UserNotFound, cancellationToken);
            held.Add(path);

            return new AdminResult(
                AdminOutcome.Success,
                Groups: [.. held.Order(StringComparer.Ordinal)],
                Added: already ? [] : [path],
                Portals: AllReadable(),
                User: user,
                Created: created);
        }
        catch (DirectoryFailure failure)
        {
            return AdminResult.Failed(failure.Outcome == AdminOutcome.UserNotFound ? AdminOutcome.Transient : failure.Outcome);
        }
    }

    public async Task<AdminResult> SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{AdminRoot}/users/{Uri.EscapeDataString(subject)}";
            var user = await GetAsync(Op.GetUser, url, AdminOutcome.UserNotFound, cancellationToken);

            // The whole representation goes back, with one field changed: a PUT of a bare {"enabled":..} is not guaranteed to leave
            // every other attribute alone across Keycloak versions.
            var body = ReplaceEnabled(user, enabled);
            await SendAsync(Op.SetEnabled, HttpMethod.Put, url, body, AdminOutcome.UserNotFound, cancellationToken);
            return new AdminResult(AdminOutcome.Success, User: ToUser(user) with { Enabled = enabled });
        }
        catch (DirectoryFailure failure)
        {
            return AdminResult.Failed(failure.Outcome);
        }
    }

    // ---- paths ----------------------------------------------------------------------------------

    internal static string Normalize(string? path) => (path ?? string.Empty).Trim().TrimEnd('/');

    private static string? ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : null;
    }

    private bool IsSeat(string path) =>
        Admin.ManagedGroupPaths.Any(m => path.Length > m.Length + 1 && path.StartsWith(m + "/", StringComparison.Ordinal));

    private bool IsManaged(string path) => IsSeat(path);

    private IReadOnlyList<PortalAccess> AllReadable() => [.. Admin.ManagedGroupPaths.Select(p => new PortalAccess(p, AdminOutcome.Success))];

    /// <summary>Success when anything was readable (the per-portal list says which); else the first failure, so a total outage is not "no members".</summary>
    private static AdminOutcome Summary(List<PortalAccess> portals) =>
        portals.Count == 0 || portals.Any(p => p.Readable) ? AdminOutcome.Success : portals[0].Outcome;

    // ---- reads ----------------------------------------------------------------------------------

    private async Task<GroupRef> FindGroupAsync(string path, CancellationToken cancellationToken)
    {
        var segments = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var group = await GetAsync(Op.FindGroup, $"{AdminRoot}/group-by-path/{segments}", AdminOutcome.GroupNotFound, cancellationToken);
        return new GroupRef(Str(group, "id") ?? string.Empty, Str(group, "name") ?? string.Empty, Str(group, "path") ?? path);
    }

    private async Task<List<GroupRef>> SeatsAsync(GroupRef root, CancellationToken cancellationToken)
    {
        var seats = new List<GroupRef>();
        var level = new List<GroupRef> { root };
        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<GroupRef>();
            foreach (var parent in level)
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var children = (await GetAsync(
                        Op.ListChildren,
                        $"{AdminRoot}/groups/{Uri.EscapeDataString(parent.Id)}/children?first={(page * PageSize).ToString(CultureInfo.InvariantCulture)}&max={PageSize.ToString(CultureInfo.InvariantCulture)}",
                        AdminOutcome.GroupNotFound,
                        cancellationToken)).EnumerateArray().ToList();
                    next.AddRange(children.Select(c => new GroupRef(Str(c, "id") ?? string.Empty, Str(c, "name") ?? string.Empty, Str(c, "path") ?? string.Empty)));
                    if (children.Count < PageSize)
                    {
                        break;
                    }
                }
            }

            seats.AddRange(next);
            level = next;
        }

        return seats;
    }

    private async Task<List<RealmUser>> MembersOfAsync(GroupRef seat, CancellationToken cancellationToken)
    {
        var users = new List<RealmUser>();
        for (var page = 0; page < MaxPages; page++)
        {
            var members = (await GetAsync(
                Op.ListMembers,
                $"{AdminRoot}/groups/{Uri.EscapeDataString(seat.Id)}/members?first={(page * PageSize).ToString(CultureInfo.InvariantCulture)}&max={PageSize.ToString(CultureInfo.InvariantCulture)}",
                AdminOutcome.GroupNotFound,
                cancellationToken)).EnumerateArray().ToList();
            users.AddRange(members.Select(ToUser));
            if (members.Count < PageSize)
            {
                break;
            }
        }

        return users;
    }

    private async Task<List<GroupRef>> CurrentManagedGroupsAsync(string subject, CancellationToken cancellationToken)
    {
        var groups = await GetAsync(Op.UserGroups, $"{AdminRoot}/users/{Uri.EscapeDataString(subject)}/groups?briefRepresentation=true&first=0&max=1000", AdminOutcome.UserNotFound, cancellationToken);
        return [.. groups.EnumerateArray()
            .Select(g => new GroupRef(Str(g, "id") ?? string.Empty, Str(g, "name") ?? string.Empty, Str(g, "path") ?? string.Empty))
            .Where(g => IsManaged(g.Path))];
    }

    // ---- HTTP -----------------------------------------------------------------------------------

    private async Task<JsonElement> GetAsync(string operation, string url, AdminOutcome onNotFound, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(operation, HttpMethod.Get, url, null, onNotFound, cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            LogRefused(_logger, operation, (int)reply.Status, AdminOutcome.Transient, "unreadable_body");
            throw new DirectoryFailure(AdminOutcome.Transient);
        }
    }

    private async Task<Reply> SendAsync(
        string operation, HttpMethod method, string url, object? body, AdminOutcome onNotFound, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(attempt > 0, cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(
                    body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogRefused(_logger, operation, null, AdminOutcome.Transient, "timeout");
                throw new DirectoryFailure(AdminOutcome.Transient);
            }
            catch (HttpRequestException)
            {
                LogRefused(_logger, operation, null, AdminOutcome.Transient, "transport");
                throw new DirectoryFailure(AdminOutcome.Transient);
            }

            using (response)
            {
                var status = response.StatusCode;
                if (status == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    // A cached token the server no longer accepts (realm key rotation, a restart): mint one afresh, once.
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    return new Reply(status, await response.Content.ReadAsStringAsync(cancellationToken), response.Headers.Location);
                }

                var outcome = Classify(status, onNotFound);
                LogRefused(_logger, operation, (int)status, outcome, "upstream_status");
                throw new DirectoryFailure(outcome);
            }
        }
    }

    /// <summary>401 and 403 are our credential, 404 is the caller's named outcome, 408/429/5xx are retryable, every other 4xx is the request itself.</summary>
    internal static AdminOutcome Classify(HttpStatusCode status, AdminOutcome onNotFound) => (int)status switch
    {
        401 or 403 => AdminOutcome.Denied,
        404 => onNotFound,
        408 or 429 or >= 500 => AdminOutcome.Transient,
        _ => AdminOutcome.UpstreamRejected,
    };

    private async Task<string> TokenAsync(bool forceNew, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (!forceNew && _token is not null && _tokenUntil > now)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/realms/{Realm}/protocol/openid-connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = Admin.Keycloak.ClientId,
                    ["client_secret"] = Admin.Keycloak.ClientSecret,
                }),
            };

            try
            {
                using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var outcome = (int)response.StatusCode is 400 or 401 or 403 ? AdminOutcome.Denied : AdminOutcome.Transient;
                    LogRefused(_logger, Op.AdminToken, (int)response.StatusCode, outcome, "upstream_status");
                    throw new DirectoryFailure(outcome);
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var token = Str(doc.RootElement, "access_token");
                if (string.IsNullOrEmpty(token))
                {
                    LogRefused(_logger, Op.AdminToken, (int)response.StatusCode, AdminOutcome.Transient, "no_access_token");
                    throw new DirectoryFailure(AdminOutcome.Transient);
                }

                var lifetime = doc.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 60;
                _token = token;
                _tokenUntil = now + TimeSpan.FromSeconds(lifetime) - TokenSkew;
                return token;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogRefused(_logger, Op.AdminToken, null, AdminOutcome.Transient, "timeout");
                throw new DirectoryFailure(AdminOutcome.Transient);
            }
            catch (HttpRequestException)
            {
                LogRefused(_logger, Op.AdminToken, null, AdminOutcome.Transient, "transport");
                throw new DirectoryFailure(AdminOutcome.Transient);
            }
            catch (JsonException)
            {
                LogRefused(_logger, Op.AdminToken, null, AdminOutcome.Transient, "unreadable_body");
                throw new DirectoryFailure(AdminOutcome.Transient);
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    // ---- representation ------------------------------------------------------------------------

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static RealmUser ToUser(JsonElement user) => new(
        Str(user, "id") ?? string.Empty,
        Str(user, "username") ?? string.Empty,
        Str(user, "email"),
        Str(user, "firstName"),
        Str(user, "lastName"),
        user.ValueKind == JsonValueKind.Object && user.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True);

    private static string ReplaceEnabled(JsonElement user, bool enabled)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in user.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields.Where(f => f.Key != "enabled"))
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteBoolean("enabled", enabled);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record GroupRef(string Id, string Name, string Path);

    private sealed record Reply(HttpStatusCode Status, string Body, Uri? Location);

    /// <summary>Carries the outcome out of the call helpers; never escapes this class.</summary>
    private sealed class DirectoryFailure(AdminOutcome outcome) : Exception
    {
        public AdminOutcome Outcome { get; } = outcome;
    }

    /// <summary>Operation names as log fields. A route template names the call without carrying a subject or an e-mail address.</summary>
    private static class Op
    {
        public const string AdminToken = "POST /realms/{realm}/protocol/openid-connect/token";
        public const string SearchUsers = "GET /users?search";
        public const string FindUserByEmail = "GET /users?email";
        public const string GetUser = "GET /users/{id}";
        public const string CreateUser = "POST /users";
        public const string SetEnabled = "PUT /users/{id}";
        public const string UserGroups = "GET /users/{id}/groups";
        public const string AddMembership = "PUT /users/{id}/groups/{groupId}";
        public const string RemoveMembership = "DELETE /users/{id}/groups/{groupId}";
        public const string FindGroup = "GET /group-by-path/{path}";
        public const string ListChildren = "GET /groups/{id}/children";
        public const string ListMembers = "GET /groups/{id}/members";
    }

    // The operation, the upstream status and the outcome are SEPARATE fields: folding them into one label would destroy the one
    // distinction (our credential vs a missing role vs an outage) that a reader of the log needs.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Keycloak admin call {Operation} did not succeed: upstream status {UpstreamStatus}, answered as {Outcome} ({Reason}).")]
    private static partial void LogRefused(ILogger logger, string operation, int? upstreamStatus, AdminOutcome outcome, string reason);
}

/// <summary>The directory when no admin client is configured: every operation says so, rather than answering empty.</summary>
internal sealed class NotConfiguredDirectoryAdmin : IDirectoryAdmin
{
    public Task<(AdminOutcome Outcome, IReadOnlyList<RealmUser> Users)> SearchUsersAsync(string term, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<(AdminOutcome, IReadOnlyList<RealmUser>)>((AdminOutcome.NotSupportedByUpstream, []));

    public Task<GroupTreeView> GetGroupTreeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GroupTreeView(AdminOutcome.NotSupportedByUpstream, [], []));

    public Task<MembersView> GetPortalMembersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new MembersView(AdminOutcome.NotSupportedByUpstream, [], []));

    public Task<AdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken) => Task.FromResult(AdminResult.NotSupported);

    public Task<AdminResult> SetGroupsAsync(string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken) =>
        Task.FromResult(AdminResult.NotSupported);

    public Task<AdminResult> CreateUserAsync(NewUser request, CancellationToken cancellationToken) => Task.FromResult(AdminResult.NotSupported);

    public Task<AdminResult> SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken) => Task.FromResult(AdminResult.NotSupported);
}
