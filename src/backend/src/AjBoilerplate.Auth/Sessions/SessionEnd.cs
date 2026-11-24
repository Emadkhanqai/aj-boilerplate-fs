namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// Why a session ended, as a portal shows it in an authentication history (docs/auth/session-ended-contract.md): "Logged out",
/// "Session expired" and "Signed out by system". The wire values are <see cref="SessionEndReasons"/>.
/// </summary>
public enum SessionEndReason
{
    /// <summary>The user pressed Logout (<c>POST /auth/logout</c>).</summary>
    LoggedOut,

    /// <summary>The session ran out of time: the idle timeout or the 12-hour absolute lifetime.</summary>
    SessionExpired,

    /// <summary>The sign-in is no longer valid: Entra/B2C refused the grant, Keycloak denied the
    /// user or left them no role, the grant it borrowed is gone, or the user was disabled.</summary>
    SignedOutBySystem,
}

/// <summary>The contract's wire values. Stored in the outbox and sent as-is; never rename one.</summary>
public static class SessionEndReasons
{
    public const string LoggedOut = "logged-out";
    public const string SessionExpired = "session-expired";
    public const string SignedOutBySystem = "signed-out-by-system";

    public static string ToWire(SessionEndReason reason) => reason switch
    {
        SessionEndReason.LoggedOut => LoggedOut,
        SessionEndReason.SessionExpired => SessionExpired,
        SessionEndReason.SignedOutBySystem => SignedOutBySystem,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static SessionEndReason? FromWire(string? value) => value switch
    {
        LoggedOut => SessionEndReason.LoggedOut,
        SessionExpired => SessionEndReason.SessionExpired,
        SignedOutBySystem => SessionEndReason.SignedOutBySystem,
        _ => null,
    };
}

/// <summary>
/// The specific check behind a <see cref="SessionEndReason"/>, sent as <c>reasonDetail</c>. Three
/// reasons are what a user is shown; the detail is what whoever investigates needs, so it is never
/// folded away ("a label is a measurement only if it distinguishes"). Slugs only: no claim, token or identifier ever goes in one.
/// </summary>
public static class SessionEndDetails
{
    // logged-out
    public const string UserLogout = "user_logout";

    // session-expired
    public const string IdleTimeout = "idle_timeout";
    public const string AbsoluteLifetime = "absolute_lifetime";

    // signed-out-by-system
    public const string EntraInvalidGrant = "entra_invalid_grant";
    public const string KeycloakDenied = "keycloak_denied";
    public const string NoClientRole = "no_client_role";
    public const string NoRefreshToken = "no_refresh_token";
    public const string RefreshLineageEnded = "refresh_lineage_ended";
    public const string UserDisabled = "user_disabled";
}

/// <summary>One session end, as decided by the code path that ended it.</summary>
/// <param name="Reason">What the portal shows.</param>
/// <param name="Detail">Which check decided it; one of <see cref="SessionEndDetails"/>.</param>
/// <param name="OccurredAt">When the session ended. For a request-driven end, the time of that
/// request; for a swept one, the deadline that passed, not the time of the sweep.</param>
public sealed record SessionEnd(SessionEndReason Reason, string Detail, DateTimeOffset OccurredAt)
{
    public static SessionEnd LoggedOut(DateTimeOffset at) => new(SessionEndReason.LoggedOut, SessionEndDetails.UserLogout, at);

    public static SessionEnd Expired(string detail, DateTimeOffset at) => new(SessionEndReason.SessionExpired, detail, at);

    public static SessionEnd BySystem(string detail, DateTimeOffset at) => new(SessionEndReason.SignedOutBySystem, detail, at);
}

/// <summary>A queued session-ended event, as the outbox holds it and the dispatcher sends it.</summary>
/// <param name="EventId">Idempotency key; stable across every retry of this event.</param>
/// <param name="SessionId">The portal-facing session id, the same value as <c>X-Usp-Session-Id</c>.</param>
/// <param name="ClientId">Keycloak client of the session, which names the receiving portal.</param>
/// <param name="Reason">Wire value, one of <see cref="SessionEndReasons"/>.</param>
/// <param name="Detail">One of <see cref="SessionEndDetails"/>.</param>
/// <param name="OccurredAt">When the session ended.</param>
/// <param name="SessionStartedAt">The session's sign-in time; null when none was recorded.</param>
/// <param name="CorrelationId">The request id of the request that ended it; null for a swept session.</param>
/// <param name="EnqueuedAt">When the event was written.</param>
/// <param name="Attempt">1 on the first send, incremented per claim.</param>
public sealed record SessionEndedEvent(
    Guid EventId,
    string SessionId,
    string ClientId,
    string Reason,
    string Detail,
    DateTimeOffset OccurredAt,
    DateTimeOffset? SessionStartedAt,
    string? CorrelationId,
    DateTimeOffset EnqueuedAt,
    int Attempt);

/// <summary>What a send concluded.</summary>
public enum SessionEndedDeliveryKind
{
    /// <summary>The portal acknowledged it (2xx), including "duplicate" and "unknown session".</summary>
    Delivered,

    /// <summary>Worth trying again: timeout, transport, 5xx, 429, 408, 404 (receiver not deployed
    /// yet), 401/403 (IAM or audience not configured yet), or no ID token available.</summary>
    Retry,

    /// <summary>The portal refused the event itself (400, 409, 413, 415, 422): resending the same
    /// bytes cannot succeed, so the event is abandoned at once.</summary>
    Permanent,
}

/// <param name="Kind">Whether it was delivered, should be retried, or cannot succeed.</param>
/// <param name="Status">The HTTP status, or null when no response arrived.</param>
/// <param name="Outcome">A slug: the receiver's own <c>outcome</c> on a 2xx, else what went wrong
/// (<c>timeout</c>, <c>transport</c>, <c>http_503</c>, <c>id_token_unavailable</c>, ...).</param>
public sealed record SessionEndedDelivery(SessionEndedDeliveryKind Kind, int? Status, string Outcome);
