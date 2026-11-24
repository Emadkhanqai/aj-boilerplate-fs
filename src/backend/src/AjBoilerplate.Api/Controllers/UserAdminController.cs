using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Administration;
using AjBoilerplate.Contracts.Admin;
using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// The Administration → Users screens' API: find people, see and change the seat groups they hold, create an
/// account with a seat, disable or enable one, and end its sessions so a change takes effect. A thin proxy over
/// the Auth service's <c>/internal/admin/*</c> (<see cref="IIdentityAdministration"/>): the API holds the admin
/// key, the browser never does, and every route here is <see cref="Policies.AdminAccess"/>.
///
/// <para>
/// <b>Upstream failures answer 502 or 503, never 403.</b> A 401/403 from the Auth service is THIS service's
/// credential or key scope failing, not the administrator's permissions (which were checked and passed before
/// any action ran), so it must not reach the browser as "you may not" — that would send the wrong person to ask
/// for a grant that changes nothing. The raw upstream status is in the log, under the response's trace id.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = Policies.AdminAccess)]
[Route("api/v1/admin")]
[Produces("application/json")]
public sealed class UserAdminController : ControllerBase
{
    private readonly IIdentityAdministration _identity;

    public UserAdminController(IIdentityAdministration identity) => _identity = identity;

    /// <summary>Search the directory by name or e-mail. A blank <c>q</c> answers an empty list without asking
    /// upstream. <c>limit</c> 1–100, default 25.</summary>
    /// <param name="q">The search term. It is personal data: sent to the Auth service as a query string, never logged.</param>
    /// <param name="limit">Most people to return.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("users")]
    [ProducesResponseType(typeof(DirectoryUsersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SearchUsers([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct)
    {
        var (outcome, users) = await _identity.SearchUsersAsync(q ?? string.Empty, limit ?? 25, ct);
        return outcome == IdentityAdminOutcome.Success
            ? Ok(new DirectoryUsersResponse([.. users.Select(ToResponse)]))
            : Respond(IdentityAdminResult.Failed(outcome));
    }

    /// <summary>Everyone who holds a seat in a portal the Auth service manages, with those seats. A portal whose
    /// <c>outcome</c> is not <c>Success</c> is missing from <c>members</c>; that is not "nobody holds a seat".</summary>
    [HttpGet("members")]
    [ProducesResponseType(typeof(DirectoryMembersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetMembers(CancellationToken ct)
    {
        var (outcome, members, portals) = await _identity.GetMembersAsync(ct);
        return outcome == IdentityAdminOutcome.Success
            ? Ok(new DirectoryMembersResponse(
                [.. members.Select(m => new DirectoryMemberResponse(ToResponse(m.User), m.Groups))], ToResponse(portals)))
            : Respond(IdentityAdminResult.Failed(outcome));
    }

    /// <summary>The seat groups the Auth service manages — the values <c>PUT …/groups</c> accepts.</summary>
    [HttpGet("groups")]
    [ProducesResponseType(typeof(DirectoryGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetGroups(CancellationToken ct)
    {
        var (outcome, groups, portals) = await _identity.GetGroupsCatalogAsync(ct);
        return outcome == IdentityAdminOutcome.Success
            ? Ok(new DirectoryGroupsResponse(
                [.. groups.Select(g => new DirectoryGroupResponse(g.Path, g.Name, g.ParentPath))], ToResponse(portals)))
            : Respond(IdentityAdminResult.Failed(outcome));
    }

    /// <summary>The groups one person holds.</summary>
    [HttpGet("users/{subject}/groups")]
    [ProducesResponseType(typeof(UserGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetUserGroups(string subject, CancellationToken ct) =>
        Respond(await _identity.GetGroupsAsync(subject, ct));

    /// <summary>Replace the groups one person holds. Send the full set; an empty array removes every seat. The
    /// response says what was added and removed. A failure can still have changed something (the Auth service
    /// adds before it removes and rolls nothing back): re-read the groups after any failure.</summary>
    [HttpPut("users/{subject}/groups")]
    [ProducesResponseType(typeof(UserGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SetUserGroups(string subject, SetUserGroupsRequest request, CancellationToken ct)
    {
        if (request?.Groups is null)
        {
            return BadRequest(Envelope(
                StatusCodes.Status400BadRequest, EnvelopeCodes.ValidationError,
                "groups is required. Send an empty array to remove the user from every group."));
        }

        return Respond(await _identity.SetGroupsAsync(subject, request.Groups, ct));
    }

    /// <summary>Give a person a seat, creating their account when the directory has none.</summary>
    [HttpPost("users")]
    [ProducesResponseType(typeof(CreatedDirectoryUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateUser(CreateDirectoryUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request.GroupPath))
        {
            return BadRequest(Envelope(
                StatusCodes.Status400BadRequest, EnvelopeCodes.ValidationError, "email and groupPath are required."));
        }

        var result = await _identity.CreateUserAsync(
            new NewDirectoryUser(
                request.Email.Trim(), request.FirstName?.Trim() ?? string.Empty, request.LastName?.Trim() ?? string.Empty,
                request.GroupPath.Trim()),
            ct);

        return result.Succeeded && result.User is not null
            ? Ok(new CreatedDirectoryUserResponse(ToResponse(result.User), result.Groups))
            : Respond(result);
    }

    /// <summary>Disable the account and end its sessions.</summary>
    [HttpPost("users/{subject}/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Disable(string subject, CancellationToken ct) =>
        Respond(await _identity.DisableAsync(subject, ct), noContent: true);

    /// <summary>Enable the account.</summary>
    [HttpPost("users/{subject}/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enable(string subject, CancellationToken ct) =>
        Respond(await _identity.EnableAsync(subject, ct), noContent: true);

    /// <summary>Flag every live session of the person for a re-exchange, so a changed seat applies without
    /// waiting for the next token renewal.</summary>
    [HttpPost("users/{subject}/refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Refresh(string subject, CancellationToken ct) =>
        Respond(IdentityAdminResult.Failed(await _identity.RefreshSessionsAsync(subject, ct)), noContent: true);

    private IActionResult Respond(IdentityAdminResult result, bool noContent = false)
    {
        switch (result.Outcome)
        {
            case IdentityAdminOutcome.Success:
                return noContent
                    ? NoContent()
                    : Ok(new UserGroupsResponse(result.Groups, result.Added, result.Removed, ToResponse(result.Portals)));

            case IdentityAdminOutcome.UserNotFound:
                return NotFound(Envelope(StatusCodes.Status404NotFound, EnvelopeCodes.NotFound, "No such user in the identity provider."));

            case IdentityAdminOutcome.GroupNotFound:
                return BadRequest(Envelope(
                    StatusCodes.Status400BadRequest, EnvelopeCodes.ValidationError, "One or more groups do not exist. Nothing was changed."));

            case IdentityAdminOutcome.EmailDomainNotAllowed:
                // One message for a malformed address, a domain not allowed and an empty allowlist, so it never
                // reveals which domains are allowed.
                return UnprocessableEntity(Envelope(
                    StatusCodes.Status422UnprocessableEntity, EnvelopeCodes.EmailDomainNotAllowed, "This e-mail domain cannot be added."));

            case IdentityAdminOutcome.NotSupportedByUpstream:
                // 501, never an empty 200: an empty success is indistinguishable from a true empty answer.
                return StatusCode(StatusCodes.Status501NotImplemented, Envelope(
                    StatusCodes.Status501NotImplemented, EnvelopeCodes.UpstreamError,
                    "The identity service is not configured for administration (its Keycloak admin client is not set up). Retrying will not help until it is."));

            case IdentityAdminOutcome.UpstreamRejected:
                return StatusCode(StatusCodes.Status502BadGateway, Envelope(
                    StatusCodes.Status502BadGateway, EnvelopeCodes.UpstreamError,
                    "The identity service rejected this request and retrying will not help. A person has to resolve it; the log has the details under this trace id."));

            case IdentityAdminOutcome.PortalNotPermitted:
            case IdentityAdminOutcome.Denied:
                // 502, not 403: the upstream's 403 is about THIS service's key scope, not about the administrator.
                return StatusCode(StatusCodes.Status502BadGateway, Envelope(
                    StatusCodes.Status502BadGateway, EnvelopeCodes.UpstreamError,
                    "This service could not complete an administrative call to the identity service. This is a configuration problem, not a permissions one — your own access is fine."));

            default:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, Envelope(
                    StatusCodes.Status503ServiceUnavailable, EnvelopeCodes.UpstreamError,
                    "Identity administration is temporarily unavailable. Try again shortly."));
        }
    }

    private static IReadOnlyList<PortalAccessResponse> ToResponse(IReadOnlyList<PortalAccess>? portals) =>
        [.. (portals ?? []).Select(p => new PortalAccessResponse(p.Path, p.Outcome.ToString()))];

    private static DirectoryUserResponse ToResponse(DirectoryUser user) =>
        new(user.Subject, user.Username, user.Email, user.FirstName, user.LastName, user.Enabled);

    private ApiResponse<object> Envelope(int status, string code, string message) => new()
    {
        IsSuccess = false,
        StatusCode = status,
        Code = code,
        Message = message,
        TraceId = HttpContext?.TraceIdentifier,
    };
}
