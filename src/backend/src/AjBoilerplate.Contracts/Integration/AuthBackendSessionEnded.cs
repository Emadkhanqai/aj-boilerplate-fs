namespace AjBoilerplate.Contracts.Integration;

/// <summary>
/// The Auth service's session-ended event (<c>docs/auth/session-ended-contract.md</c>). Every property is
/// nullable on purpose: a missing field must reach the receiver's own rules, which decide what is a 400,
/// instead of being refused by model binding — a 400 makes the sender abandon the event for good.
/// </summary>
public sealed record AuthBackendSessionEndedRequest(
    int? SchemaVersion,
    string? EventType,
    string? EventId,
    string? SessionId,
    string? Reason,
    string? ReasonDetail,
    string? OccurredAtUtc,
    string? SessionStartedAtUtc,
    string? IdentityProvider,
    string? PortalClientId,
    string? CorrelationId);

/// <summary>The receiver's answer, sent BARE — not in the <c>ApiResponse</c> envelope — because the sender
/// reads <c>outcome</c> at the top level of the body. <c>outcome</c> is <c>recorded</c>, <c>duplicate</c> or
/// <c>ignored-unknown-session</c>.</summary>
public sealed record AuthBackendSessionEndedResponse(string Outcome);
