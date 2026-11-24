namespace AjBoilerplate.Auth.Sql;

/// <summary>
/// One row of <c>auth.SessionEndedOutbox</c>: a session end waiting to be told to, or already
/// told to, the portal it belongs to (docs/auth/session-ended-contract.md).
/// </summary>
/// <remarks>
/// Written in the same transaction as the delete of the session row it describes, so a session
/// can never end without its event, and an event can never exist for a session that did not end.
/// Holds no credential, no storage key and no user identifier (no subject, no email): <see cref="SessionId"/> is the portal-facing HMAC (the
/// <c>X-Usp-Session-Id</c> value), which cannot be turned back into a lookup of anything.
/// </remarks>
internal sealed class SessionEndedEventRow
{
    public Guid EventId { get; set; }

    /// <summary>32 lowercase hex: the same value the portal recorded "Session created" against.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Keycloak client of the session; resolves the receiving portal at send time.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Wire value: <c>logged-out</c>, <c>session-expired</c> or <c>signed-out-by-system</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The check that decided it, a slug.</summary>
    public string Detail { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The session's sign-in time; null when none was ever recorded.</summary>
    public DateTimeOffset? SessionStartedAt { get; set; }

    /// <summary>The request id of the request that ended the session; null for a swept one.</summary>
    public string? CorrelationId { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    /// <summary>Sends attempted so far (incremented when an instance claims the row).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// When the row is next due. Doubles as the claim: an instance that claims a row moves this
    /// forward by the lease, conditionally on the value it read, so a second instance's claim of
    /// the same row affects nothing.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>The last send's outcome slug (<c>recorded</c>, <c>http_503</c>, <c>timeout</c>, ...).</summary>
    public string? LastOutcome { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>Set when the event was given up on: refused by the portal, too old, or no receiver.</summary>
    public DateTimeOffset? AbandonedAt { get; set; }
}
