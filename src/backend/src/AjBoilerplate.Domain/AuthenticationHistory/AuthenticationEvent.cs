using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.AuthenticationHistory;

/// <summary>
/// One sign-in: a person, a gateway session, and when that session began. The gateway issues one
/// session id per sign-in and keeps it stable across token renewals, so there is exactly one row per
/// session — never one per request and never one per renewed token.
///
/// <para>
/// <see cref="Subject"/> is the identity provider's subject stored verbatim, with no foreign key,
/// because users live in the IdP and not in this schema.
/// </para>
///
/// <para>
/// <b>The row also records how the session ended</b>, in the same row (<see cref="EndedAt"/>,
/// <see cref="EndReason"/>): the Auth service reports each ending to
/// <c>POST /api/integration/auth-backend/session-ended</c>. This is the shape USP-Core settled on
/// (2026-11-07) — one row per session, never a "created" row plus an "ended" row — reached here by a
/// single nullable-columns row instead of an event table, because the boilerplate has one event kind
/// per session and a unique index on the session id already.
/// </para>
/// </summary>
public sealed class AuthenticationEvent
{
    public const int MaxSubjectLength = 200;

    public const int MaxSessionIdLength = 128;

    public const int MaxDisplayNameLength = 200;

    public const int MaxEndReasonDetailLength = 64;

    public Guid Id { get; private set; }

    public string Subject { get; private set; }

    public string SessionId { get; private set; }

    /// <summary>When the gateway says the session began (<c>X-Usp-Session-Started-At</c>).</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>When this API first saw the session. Later than <see cref="StartedAt"/>, by however
    /// long the person took to make a first request.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>The person's name as the token carried it when they signed in. Display only; null
    /// when the token had none (and on rows recorded before this column existed).</summary>
    public string? DisplayName { get; private set; }

    /// <summary>When the session ended, or null while it is open (or the Auth service never said).</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>How it ended: one of <see cref="SessionEndReasons"/>. Null while open.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Which check decided the ending (<c>idle_timeout</c>, <c>user_logout</c>, …): for
    /// investigation, never shown to a user. Only a slug-shaped value is kept.</summary>
    public string? EndReasonDetail { get; private set; }

    /// <summary>The Auth service's <c>eventId</c> for the ending — the key it joins its own log on.</summary>
    public Guid? EndEventId { get; private set; }

    // The ending is written by AuthenticationEventRepository.EndAsync as ONE conditional UPDATE
    // (… WHERE EndedAt IS NULL), not by a method here: a read-modify-write on this row would let two
    // concurrent deliveries of one event both report "recorded".

    private AuthenticationEvent()
    {
        Subject = SessionId = string.Empty;
    }

    private AuthenticationEvent(
        Guid id, string subject, string? displayName, string sessionId, DateTimeOffset startedAt, DateTimeOffset recordedAt)
    {
        Id = id;
        Subject = subject;
        DisplayName = displayName;
        SessionId = sessionId;
        StartedAt = startedAt;
        RecordedAt = recordedAt;
    }

    public static AuthenticationEvent Record(
        Guid id, string subject, string sessionId, DateTimeOffset startedAt, DateTimeOffset nowUtc, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new DomainException("A sign-in needs a subject.");
        }

        if (!IsSessionId(sessionId))
        {
            throw new DomainException("A session id is 1 to 128 letters, digits, '-' or '_'.");
        }

        var trimmed = subject.Trim();
        return new AuthenticationEvent(
            id,
            trimmed.Length <= MaxSubjectLength ? trimmed : trimmed[..MaxSubjectLength],
            CleanName(displayName),
            sessionId,
            startedAt,
            nowUtc);
    }

    private static string? CleanName(string? displayName)
    {
        var name = displayName?.Trim();
        return string.IsNullOrEmpty(name) ? null : name.Length <= MaxDisplayNameLength ? name : name[..MaxDisplayNameLength];
    }

    /// <summary>The shape a gateway session id may take. Checked before it becomes a cache key or a
    /// column value, because it arrives in a request header.</summary>
    public static bool IsSessionId(string? value) =>
        value is { Length: > 0 and <= MaxSessionIdLength }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_');

    /// <summary>
    /// The Result the history shows: <c>Failure</c> only when the system signed the person out, because
    /// then their sign-in was no longer accepted; <c>Success</c> for an open session, a logout and an
    /// expiry (the session ending as designed). E-Budgeting's and CAD's mapping, verbatim.
    /// </summary>
    public string Result => EndReason == SessionEndReasons.SignedOutBySystem
        ? AuthenticationEventResults.Failure
        : AuthenticationEventResults.Success;

    /// <summary>Lowercase letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, at most
    /// <see cref="MaxEndReasonDetailLength"/>. Anything else could be free text, so it is not stored —
    /// but it never rejects the event.</summary>
    public static bool IsSlug(string? value) =>
        value is { Length: > 0 and <= MaxEndReasonDetailLength }
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');
}

/// <summary>The three ways a session ends, as the Auth service's wire values. Stored as-is; never
/// rename one. Shown as "Logged out", "Session expired" and "Signed out by system".</summary>
public static class SessionEndReasons
{
    public const string LoggedOut = "logged-out";
    public const string SessionExpired = "session-expired";
    public const string SignedOutBySystem = "signed-out-by-system";

    public static readonly IReadOnlyList<string> All = [LoggedOut, SessionExpired, SignedOutBySystem];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

public static class AuthenticationEventResults
{
    public const string Success = "Success";
    public const string Failure = "Failure";
}
