using AjBoilerplate.Api.Identity;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// The Auth service's session-ended events (<c>docs/auth/session-ended-contract.md</c>). A MACHINE surface,
/// authenticated by <c>X-Auth-Events-Key</c> or, opt-in, a Google ID token from one named service account;
/// it resolves to a fixed service principal that satisfies <see cref="Policies.AuthBackendServiceAccess"/> and
/// nothing else. Answers 404 while neither is configured (<see cref="AuthBackendIntegrationEnabledMiddleware"/>).
///
/// <para>
/// <b>Not versioned, deliberately</b>: the sender's contract names this exact path, and a version segment would
/// be one more setting to keep equal on both sides. Called on the API's own origin, never through the gateway;
/// the sign-in recorder skips <c>/api/integration/</c>.
/// </para>
/// <para>
/// <b>Status codes are the sender's retry policy.</b> It retries 401/403/404/408/429/5xx for 48 hours and
/// abandons any other 4xx at once, so this answers 400 only for an event that can never be valid, and 200 — with
/// <c>recorded</c>, <c>duplicate</c> or <c>ignored-unknown-session</c> — for everything else. The success body
/// is bare (<see cref="BareSuccessResponseAttribute"/>) because the sender reads <c>outcome</c> at the top level.
/// </para>
/// <para>
/// <b>Logged</b>: the surface, outcome, reason and trace id as structured fields, plus the event id (the sender
/// logs the same id, so the two sides join). <b>Never</b>: the key, a token, the session id, or any body text.
/// </para>
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthBackendAuthenticationDefaults.Schemes, Policy = Policies.AuthBackendServiceAccess)]
[Route("api/integration/auth-backend")]
[Produces("application/json")]
public sealed class AuthBackendIntegrationController : ControllerBase
{
    private readonly IAuthenticationHistoryService _history;
    private readonly IOptions<AuthBackendIntegrationOptions> _options;
    private readonly IOptions<KeycloakOptions> _keycloak;
    private readonly ILogger<AuthBackendIntegrationController> _logger;

    public AuthBackendIntegrationController(
        IAuthenticationHistoryService history, IOptions<AuthBackendIntegrationOptions> options,
        IOptions<KeycloakOptions> keycloak, ILogger<AuthBackendIntegrationController> logger)
    {
        _history = history;
        _options = options;
        _keycloak = keycloak;
        _logger = logger;
    }

    /// <summary>Record how one session ended. Idempotent on the session: the first event for it wins, every
    /// other is answered <c>duplicate</c>.</summary>
    [HttpPost("session-ended")]
    [BareSuccessResponse]
    [ProducesResponseType(typeof(AuthBackendSessionEndedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostSessionEnded(AuthBackendSessionEndedRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedClient = string.IsNullOrWhiteSpace(_options.Value.PortalClientId)
            ? _keycloak.Value.ClientId
            : _options.Value.PortalClientId;

        var result = await _history.RecordSessionEndedAsync(
            new SessionEndedNotice(
                request.SchemaVersion, request.EventType, request.EventId, request.SessionId, request.Reason,
                request.ReasonDetail, request.OccurredAtUtc, request.PortalClientId),
            expectedClient,
            ct);

        // Only a parsed id is logged: a malformed one is caller text, and caller text never reaches a log line.
        var eventId = Guid.TryParse(request.EventId, out var parsed) ? parsed.ToString("D") : null;
        if (result.Outcome == SessionEndedOutcome.Invalid)
        {
            _logger.LogWarning(
                "Refused a session-ended event. Surface={Surface} Outcome={Outcome} InvalidReason={InvalidReason} EventId={EventId} TraceId={TraceId}",
                AuthBackendAuthenticationDefaults.SurfaceName, "invalid", result.InvalidReason, eventId, HttpContext.TraceIdentifier);
            throw new FluentValidation.ValidationException(
            [
                new FluentValidation.Results.ValidationFailure(
                    result.InvalidReason, "The session-ended event is not valid: " + result.InvalidReason + ".")
                {
                    ErrorCode = "SESSION_ENDED_EVENT_INVALID",
                },
            ]);
        }

        var outcome = result.Outcome switch
        {
            SessionEndedOutcome.Recorded => "recorded",
            SessionEndedOutcome.Duplicate => "duplicate",
            _ => "ignored-unknown-session",
        };
        _logger.LogInformation(
            "Received a session-ended event. Surface={Surface} Outcome={Outcome} Reason={Reason} EventId={EventId} TraceId={TraceId}",
            AuthBackendAuthenticationDefaults.SurfaceName, outcome, result.Reason, eventId, HttpContext.TraceIdentifier);
        return Ok(new AuthBackendSessionEndedResponse(outcome));
    }
}
