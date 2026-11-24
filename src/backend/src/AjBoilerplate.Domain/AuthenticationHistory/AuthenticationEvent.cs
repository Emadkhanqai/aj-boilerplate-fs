using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.AuthenticationHistory;

/// <summary>
/// One sign-in: a person, a gateway session, and when that session began. The gateway issues one
/// session id per sign-in and keeps it stable across token renewals, so there is exactly one row per
/// session — never one per request and never one per renewed token.
///
/// <para>
/// <see cref="Subject"/> is the identity provider's subject stored verbatim, with no foreign key,
/// because users live in the IdP and not in this schema. A session ending is not recorded here: this
/// is the sign-in log, nothing more.
/// </para>
/// </summary>
public sealed class AuthenticationEvent
{
    public const int MaxSubjectLength = 200;

    public const int MaxSessionIdLength = 128;

    public Guid Id { get; private set; }

    public string Subject { get; private set; }

    public string SessionId { get; private set; }

    /// <summary>When the gateway says the session began (<c>X-Usp-Session-Started-At</c>).</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>When this API first saw the session. Later than <see cref="StartedAt"/>, by however
    /// long the person took to make a first request.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    private AuthenticationEvent()
    {
        Subject = SessionId = string.Empty;
    }

    private AuthenticationEvent(Guid id, string subject, string sessionId, DateTimeOffset startedAt, DateTimeOffset recordedAt)
    {
        Id = id;
        Subject = subject;
        SessionId = sessionId;
        StartedAt = startedAt;
        RecordedAt = recordedAt;
    }

    public static AuthenticationEvent Record(
        Guid id, string subject, string sessionId, DateTimeOffset startedAt, DateTimeOffset nowUtc)
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
            sessionId,
            startedAt,
            nowUtc);
    }

    /// <summary>The shape a gateway session id may take. Checked before it becomes a cache key or a
    /// column value, because it arrives in a request header.</summary>
    public static bool IsSessionId(string? value) =>
        value is { Length: > 0 and <= MaxSessionIdLength }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_');
}
