using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Identity;

/// <summary>
/// <see cref="IIdentityAdministration"/> over the Auth service's <c>/internal/admin/*</c>, authenticated by
/// <c>X-Admin-Api-Key</c>. Ported from USP-Core's <c>AuthBackendIdentityAdministration</c>, which signs with a
/// Google ID token instead; the paths and bodies are the same.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status fold is for the response only.</b> Upstream 401, 403, 409 and 502 become different outcomes
/// here, and the API folds them again into one user-facing message each — but they have different fixes (a
/// rotated key, a key scope, an ambiguous account, the Auth service's own upstream), so the raw status is
/// logged as <c>upstreamStatus</c> BEFORE it is folded. Logging the outcome in place of the status is the one
/// edit that destroys that evidence while every test stays green.
/// </para>
/// <para>
/// <b>What is logged:</b> the route TEMPLATE (<c>internal/admin/users/{subject}/groups</c>), the status and the
/// correlation id. Never the live path, a query string or a body — the subject, a search term and an e-mail
/// address are all personal data — and never the key.
/// </para>
/// </remarks>
public sealed partial class AuthServiceIdentityAdministration : IIdentityAdministration
{
    public const string ApiKeyHeader = "X-Admin-Api-Key";

    /// <summary>The words the Auth service's "no seat" 404 carries. Matched, never logged. Any other 404 is
    /// the admin surface being switched off (a bare 404) or a wrong route, and must not read as "this person
    /// holds nothing".</summary>
    internal const string NoSeatMarker = "holds no seat in any portal";

    private const int MaxFailureBodyBytes = 16 * 1024;

    private readonly HttpClient _http;
    private readonly AuthServiceOptions _options;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<AuthServiceIdentityAdministration> _logger;

    public AuthServiceIdentityAdministration(
        HttpClient http, IOptions<AuthServiceOptions> options, ICorrelationContext correlation,
        ILogger<AuthServiceIdentityAdministration> logger)
    {
        _http = http;
        _options = options.Value;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryUser> Users)> SearchUsersAsync(
        string term, int limit, CancellationToken cancellationToken)
    {
        // A blank term never reaches the network: the Auth service answers it with an empty list anyway.
        if (string.IsNullOrWhiteSpace(term))
        {
            return (IdentityAdminOutcome.Success, []);
        }

        // The TERM is in the query string and is somebody's name or address; the route template is logged.
        var path = $"internal/admin/users?q={Uri.EscapeDataString(term.Trim())}&limit={Math.Clamp(limit, 1, 100)}";
        var (outcome, body) = await ReadAsync<UsersBody>(path, "internal/admin/users", cancellationToken);
        return (outcome, body?.Users ?? []);
    }

    public async Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryGroup> Groups, IReadOnlyList<PortalAccess> Portals)>
        GetGroupsCatalogAsync(CancellationToken cancellationToken)
    {
        var (outcome, body) = await ReadAsync<GroupCatalogBody>("internal/admin/groups", "internal/admin/groups", cancellationToken);
        return (outcome, body?.Groups ?? [], ToPortals(body?.Portals));
    }

    public async Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryMember> Members, IReadOnlyList<PortalAccess> Portals)>
        GetMembersAsync(CancellationToken cancellationToken)
    {
        var (outcome, body) = await ReadAsync<MembersBody>("internal/admin/members", "internal/admin/members", cancellationToken);
        return (outcome, body?.Members ?? [], ToPortals(body?.Portals));
    }

    public Task<IdentityAdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken) =>
        SendForSubjectAsync(HttpMethod.Get, "groups", subject, content: null, cancellationToken);

    public Task<IdentityAdminResult> SetGroupsAsync(
        string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupPaths);
        return SendForSubjectAsync(HttpMethod.Put, "groups", subject, JsonContent.Create(new GroupsBody([.. groupPaths])), cancellationToken);
    }

    public Task<IdentityAdminResult> DisableAsync(string subject, CancellationToken cancellationToken) =>
        SendForSubjectAsync(HttpMethod.Post, "disable", subject, content: null, cancellationToken);

    public Task<IdentityAdminResult> EnableAsync(string subject, CancellationToken cancellationToken) =>
        SendForSubjectAsync(HttpMethod.Post, "enable", subject, content: null, cancellationToken);

    public async Task<IdentityAdminOutcome> RefreshSessionsAsync(string subject, CancellationToken cancellationToken) =>
        (await SendForSubjectAsync(HttpMethod.Post, "refresh", subject, content: null, cancellationToken)).Outcome;

    /// <summary>Puts a person in a seat, creating the account when there is none. The e-mail travels in the
    /// BODY, never in the path or a query string, which every hop's access log would keep.</summary>
    public async Task<IdentityAdminResult> CreateUserAsync(NewDirectoryUser request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "internal/admin/users";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new CreateUserBody(request.Email, request.FirstName, request.LastName, request.GroupPath)),
        };

        var (response, failure) = await SendAsync(message, endpoint, cancellationToken);
        if (response is null)
        {
            return IdentityAdminResult.Failed(failure);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await TryReadJsonAsync<CreatedUserBody>(response, cancellationToken);

                // Accepted but not readable back: reported as transient rather than as success, because "created"
                // with no subject gives the caller nothing to act on — and the add is idempotent, so a retry is safe.
                return body?.User is null
                    ? IdentityAdminResult.Failed(IdentityAdminOutcome.Unavailable)
                    : new IdentityAdminResult(IdentityAdminOutcome.Success, body.Groups ?? [], [], [], null, body.User);
            }

            var failureBody = await ReadFailureBodyAsync(response, cancellationToken);
            LogRefused(_logger, endpoint, (int)response.StatusCode, _correlation.CorrelationId);
            return IdentityAdminResult.Failed(
                response.StatusCode == HttpStatusCode.UnprocessableEntity && IsDomainRefusal(failureBody)
                    ? IdentityAdminOutcome.EmailDomainNotAllowed
                    : Classify(response.StatusCode));
        }
    }

    private async Task<(IdentityAdminOutcome Outcome, T? Body)> ReadAsync<T>(string path, string endpoint, CancellationToken cancellationToken)
        where T : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        var (response, failure) = await SendAsync(message, endpoint, cancellationToken);
        if (response is null)
        {
            return (failure, null);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await TryReadJsonAsync<T>(response, cancellationToken);

                // A 200 that cannot be read is the upstream misbehaving, not an empty directory.
                return body is null ? (IdentityAdminOutcome.Unavailable, null) : (IdentityAdminOutcome.Success, body);
            }

            LogRefused(_logger, endpoint, (int)response.StatusCode, _correlation.CorrelationId);
            return (Classify(response.StatusCode), null);
        }
    }

    private async Task<IdentityAdminResult> SendForSubjectAsync(
        HttpMethod method, string action, string subject, HttpContent? content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // The route TEMPLATE, not the live path: the subject is a person's identifier.
        var endpoint = $"internal/admin/users/{{subject}}/{action}";
        using var message = new HttpRequestMessage(method, $"internal/admin/users/{Uri.EscapeDataString(subject)}/{action}")
        {
            Content = content,
        };

        var (response, failure) = await SendAsync(message, endpoint, cancellationToken);
        if (response is null)
        {
            return IdentityAdminResult.Failed(failure);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return new IdentityAdminResult(IdentityAdminOutcome.Success, [], [], []);
                }

                var body = await TryReadJsonAsync<GroupsResult>(response, cancellationToken);
                return new IdentityAdminResult(
                    IdentityAdminOutcome.Success, body?.Groups ?? [], body?.Added ?? [], body?.Removed ?? [], ToPortals(body?.Portals));
            }

            // ONE read of the body, bounded, kept as a string: the "no seat" marker and the partial-write
            // extensions both need it, and a response stream can only be read once.
            var failureBody = await ReadFailureBodyAsync(response, cancellationToken);
            LogRefused(_logger, endpoint, (int)response.StatusCode, _correlation.CorrelationId);

            // A failed write is not necessarily a write that did nothing. The upstream issues adds before
            // removes and rolls nothing back, so it reports a PARTIAL application in the failure body;
            // dropping that would tell an administrator nothing happened when somebody's access changed.
            var partial = ParsePartial(failureBody);
            return new IdentityAdminResult(
                ClassifySubjectCall(response.StatusCode, failureBody),
                partial?.Groups ?? [], partial?.Added ?? [], partial?.Removed ?? [], ToPortals(partial?.Portals));
        }
    }

    /// <summary>Sends with the key. A null response comes with the outcome to report; the failure is already logged.</summary>
    private async Task<(HttpResponseMessage? Response, IdentityAdminOutcome Failure)> SendAsync(
        HttpRequestMessage message, string endpoint, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            LogNotConfigured(_logger, endpoint, _correlation.CorrelationId);
            return (null, IdentityAdminOutcome.Unavailable);
        }

        message.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.AdminApiKey);
        try
        {
            return (await _http.SendAsync(message, cancellationToken), IdentityAdminOutcome.Success);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The exception TYPE only: its message can carry the request URI, which carries a search term.
            LogUnreachable(_logger, endpoint, ex.GetType().Name, _correlation.CorrelationId);
            return (null, IdentityAdminOutcome.Unavailable);
        }
    }

    private static IdentityAdminOutcome Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => IdentityAdminOutcome.Unavailable,
        HttpStatusCode.BadRequest => IdentityAdminOutcome.GroupNotFound,
        HttpStatusCode.Forbidden => IdentityAdminOutcome.PortalNotPermitted,
        HttpStatusCode.Conflict => IdentityAdminOutcome.UpstreamRejected,
        HttpStatusCode.Unauthorized or HttpStatusCode.BadGateway => IdentityAdminOutcome.Denied,
        HttpStatusCode.NotImplemented => IdentityAdminOutcome.NotSupportedByUpstream,
        _ => IdentityAdminOutcome.Unavailable,
    };

    /// <summary><see cref="Classify"/> for a call keyed by a subject, where a 404 is two different answers: only
    /// the Auth service's own "no seat" 404 is <see cref="IdentityAdminOutcome.UserNotFound"/>.</summary>
    private static IdentityAdminOutcome ClassifySubjectCall(HttpStatusCode status, string? body) =>
        status == HttpStatusCode.NotFound && body is not null && body.Contains(NoSeatMarker, StringComparison.Ordinal)
            ? IdentityAdminOutcome.UserNotFound
            : Classify(status);

    // A row that does not parse is kept as Unavailable rather than dropped: dropping it would make the portal
    // look like one the upstream never mentioned, which a screen renders as "not listed" — a reassuring
    // answer derived from a parse failure.
    private static IReadOnlyList<PortalAccess> ToPortals(IReadOnlyList<PortalCoverage>? rows) =>
        [.. (rows ?? []).Select(row => new PortalAccess(
            row.Path,
            Enum.TryParse<IdentityAdminOutcome>(row.Outcome, ignoreCase: true, out var parsed)
                ? parsed
                : IdentityAdminOutcome.Unavailable))];

    private static bool IsDomainRefusal(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "EMAIL_DOMAIN_NOT_ALLOWED", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GroupsResult? ParsePartial(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GroupsResult>(body);
        }
        catch (JsonException)
        {
            // Plain text, an HTML error page, or a document cut by the bound: nothing extra to report.
            return null;
        }
    }

    private static async Task<T?> TryReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadFailureBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxFailureBodyBytes, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            // This path has ALREADY failed and the read only explains it; a secondary fault must not replace it.
            return null;
        }
    }

    private sealed record GroupsBody([property: JsonPropertyName("groups")] IReadOnlyList<string> Groups);

    private sealed record UsersBody([property: JsonPropertyName("users")] IReadOnlyList<DirectoryUser>? Users);

    private sealed record PortalCoverage(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("outcome")] string Outcome);

    private sealed record GroupCatalogBody(
        [property: JsonPropertyName("groups")] IReadOnlyList<DirectoryGroup>? Groups,
        [property: JsonPropertyName("portals")] IReadOnlyList<PortalCoverage>? Portals);

    private sealed record MembersBody(
        [property: JsonPropertyName("members")] IReadOnlyList<DirectoryMember>? Members,
        [property: JsonPropertyName("portals")] IReadOnlyList<PortalCoverage>? Portals);

    private sealed record CreateUserBody(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("firstName")] string FirstName,
        [property: JsonPropertyName("lastName")] string LastName,
        [property: JsonPropertyName("groupPath")] string GroupPath);

    private sealed record CreatedUserBody(
        [property: JsonPropertyName("user")] DirectoryUser? User,
        [property: JsonPropertyName("groups")] IReadOnlyList<string>? Groups);

    private sealed record GroupsResult(
        [property: JsonPropertyName("groups")] IReadOnlyList<string>? Groups,
        [property: JsonPropertyName("added")] IReadOnlyList<string>? Added,
        [property: JsonPropertyName("removed")] IReadOnlyList<string>? Removed,
        [property: JsonPropertyName("portals")] IReadOnlyList<PortalCoverage>? Portals);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "AuthService:BaseUrl or AuthService:AdminApiKey is not configured; identity administration is unavailable. endpoint={Endpoint} correlationId={CorrelationId}")]
    private static partial void LogNotConfigured(ILogger logger, string endpoint, string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Auth service unreachable. endpoint={Endpoint} error={ErrorType} correlationId={CorrelationId}")]
    private static partial void LogUnreachable(ILogger logger, string endpoint, string errorType, string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Auth service refused an admin call. endpoint={Endpoint} upstreamStatus={UpstreamStatus} correlationId={CorrelationId}")]
    private static partial void LogRefused(ILogger logger, string endpoint, int upstreamStatus, string? correlationId);
}
