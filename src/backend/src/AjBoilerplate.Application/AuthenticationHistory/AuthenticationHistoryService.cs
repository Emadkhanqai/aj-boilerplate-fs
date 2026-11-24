using System.Globalization;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Domain.AuthenticationHistory;
using FluentValidation;

namespace AjBoilerplate.Application.AuthenticationHistory;

/// <summary>Filters, sort and paging for the sign-in history. <paramref name="FromUtc"/> is inclusive and
/// <paramref name="ToUtc"/> exclusive, both on the sign-in time, so two adjacent windows never share a session.</summary>
public sealed record AuthenticationHistoryQuery(
    string? UserId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? SortBy, string? SortDir, int Page, int PageSize);

/// <summary>The repository's form of a query: the sort already parsed.</summary>
public sealed record AuthenticationHistoryFilter(
    string? UserId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
    AuthenticationHistorySortField SortField, bool SortDescending, int Page, int PageSize);

/// <summary>One session in the history. <c>EndedAtUtc</c>, <c>EndReason</c> and <c>DurationSeconds</c> are null
/// while the session is open (or the Auth service never reported its ending).</summary>
public sealed record AuthenticationSessionDto(
    Guid Id, string UserId, string? UserDisplayName, DateTimeOffset SignedInAtUtc, DateTimeOffset? EndedAtUtc,
    string? EndReason, string? EndReasonDetail, string Result, long? DurationSeconds);

/// <summary>A person the history names. <c>UserDisplayName</c> is the name on one of their sessions (the
/// alphabetically last, which is stable), not necessarily the latest.</summary>
public sealed record AuthenticationHistoryUser(string UserId, string? UserDisplayName);

/// <summary>The Auth service's session-ended event, as received. Every field is raw and nullable on
/// purpose: the service decides what is malformed for good, because a 400 makes the sender abandon the
/// event — the line between "reject" and "accept" belongs to a rule, not to model binding.</summary>
public sealed record SessionEndedNotice(
    int? SchemaVersion, string? EventType, string? EventId, string? SessionId, string? Reason, string? ReasonDetail,
    string? OccurredAtUtc, string? PortalClientId);

public enum SessionEndedOutcome
{
    Recorded,
    Duplicate,

    /// <summary>No sign-in row for that session. Nothing written — normal, not an error.</summary>
    IgnoredUnknownSession,

    /// <summary>The event can never be valid: answered 400, which the sender does not retry.</summary>
    Invalid,
}

/// <param name="InvalidReason">For <see cref="SessionEndedOutcome.Invalid"/>: a slug naming the failed check
/// (<c>schema-version</c>, <c>event-type</c>, …). Never an input value.</param>
/// <param name="Reason">The ending's reason when it was well-formed, for the log.</param>
public sealed record SessionEndedResult(SessionEndedOutcome Outcome, string? InvalidReason = null, string? Reason = null);

public interface IAuthenticationHistoryService
{
    Task<PagedResult<AuthenticationSessionDto>> ListAsync(AuthenticationHistoryQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthenticationHistoryUser>> UsersAsync(CancellationToken cancellationToken);

    /// <summary><paramref name="expectedPortalClientId"/> blank means "do not check" (the boilerplate has no
    /// client id configured).</summary>
    Task<SessionEndedResult> RecordSessionEndedAsync(
        SessionEndedNotice notice, string? expectedPortalClientId, CancellationToken cancellationToken);
}

public sealed class AuthenticationHistoryQueryValidator : AbstractValidator<AuthenticationHistoryQuery>
{
    public AuthenticationHistoryQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).PageSizeInRange();
        RuleFor(q => q).Must(q => q.FromUtc is not { } f || q.ToUtc is not { } t || f <= t)
            .WithName("from").WithMessage("'from' must not be after 'to'.");
        RuleFor(q => q).Must(q => AuthenticationHistorySort.TryParse(q.SortBy, q.SortDir, out _, out _))
            .WithName("sortBy")
            .WithMessage($"sortBy must be one of: {string.Join(", ", AuthenticationHistorySort.Keys)}; sortDir must be asc or desc.");
    }
}

internal static class PagingRules
{
    public static IRuleBuilderOptions<T, int> PageSizeInRange<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, Paging.MaxPageSize).WithMessage($"pageSize must be between 1 and {Paging.MaxPageSize}.");
}

public sealed class AuthenticationHistoryService : IAuthenticationHistoryService
{
    /// <summary>The contract's exact form: <c>yyyy-MM-ddTHH:mm:ssZ</c>, UTC, whole seconds.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>The only <c>schemaVersion</c> this receiver understands.</summary>
    public const int SessionEndedSchemaVersion = 1;

    /// <summary>The only <c>eventType</c> the Auth service sends.</summary>
    public const string SessionEndedEventType = "session.ended";

    private readonly IAuthenticationEventRepository _events;
    private readonly IValidator<AuthenticationHistoryQuery> _validator;

    public AuthenticationHistoryService(IAuthenticationEventRepository events, IValidator<AuthenticationHistoryQuery> validator)
    {
        _events = events;
        _validator = validator;
    }

    public async Task<PagedResult<AuthenticationSessionDto>> ListAsync(
        AuthenticationHistoryQuery query, CancellationToken cancellationToken)
    {
        await _validator.ValidateAndThrowAsync(query, cancellationToken);
        AuthenticationHistorySort.TryParse(query.SortBy, query.SortDir, out var field, out var descending);

        var (items, total) = await _events.ListAsync(
            new AuthenticationHistoryFilter(
                string.IsNullOrWhiteSpace(query.UserId) ? null : query.UserId.Trim(),
                query.FromUtc, query.ToUtc, field, descending, query.Page, query.PageSize),
            cancellationToken);

        return new PagedResult<AuthenticationSessionDto>(items.Select(ToDto).ToList(), total, query.Page, query.PageSize);
    }

    public Task<IReadOnlyList<AuthenticationHistoryUser>> UsersAsync(CancellationToken cancellationToken) =>
        _events.UsersAsync(cancellationToken);

    public async Task<SessionEndedResult> RecordSessionEndedAsync(
        SessionEndedNotice notice, string? expectedPortalClientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var invalid = InvalidReason(notice, expectedPortalClientId, out var eventId, out var endedAt);
        if (invalid is not null)
        {
            return new SessionEndedResult(SessionEndedOutcome.Invalid, invalid);
        }

        var written = await _events.EndAsync(
            notice.SessionId!, eventId, notice.Reason!, notice.ReasonDetail, endedAt, cancellationToken);
        return new SessionEndedResult(
            written switch
            {
                SessionEndWrite.Ended => SessionEndedOutcome.Recorded,
                SessionEndWrite.AlreadyEnded => SessionEndedOutcome.Duplicate,
                _ => SessionEndedOutcome.IgnoredUnknownSession,
            },
            Reason: notice.Reason);
    }

    /// <summary>
    /// The checks the contract makes 400s, in its order, each with its own slug so a rejection says WHICH check
    /// failed. Deliberately NOT checked, because a 400 makes the sender abandon the event: <c>reasonDetail</c>
    /// (new values may be added), <c>identityProvider</c>, <c>sessionStartedAtUtc</c> and the UUID VERSION of
    /// <c>eventId</c>.
    /// </summary>
    private static string? InvalidReason(
        SessionEndedNotice notice, string? expectedPortalClientId, out Guid eventId, out DateTimeOffset endedAt)
    {
        eventId = Guid.Empty;
        endedAt = default;
        if (notice.SchemaVersion != SessionEndedSchemaVersion)
        {
            return "schema-version";
        }

        if (!string.Equals(notice.EventType, SessionEndedEventType, StringComparison.Ordinal))
        {
            return "event-type";
        }

        if (!Guid.TryParse(notice.EventId, out eventId) || eventId == Guid.Empty)
        {
            return "event-id";
        }

        if (!AuthenticationEvent.IsSessionId(notice.SessionId))
        {
            return "session-id";
        }

        if (!SessionEndReasons.IsKnown(notice.Reason))
        {
            return "reason";
        }

        if (!TryParseUtc(notice.OccurredAtUtc, out endedAt))
        {
            return "occurred-at";
        }

        return string.IsNullOrWhiteSpace(expectedPortalClientId)
            || string.Equals(notice.PortalClientId?.Trim(), expectedPortalClientId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : "portal-client-id";
    }

    /// <summary>The contract's exact form first; any other parseable instant too (read as UTC when it has no
    /// offset), because refusing a real instant for its formatting would lose the event for good.</summary>
    private static bool TryParseUtc(string? value, out DateTimeOffset utc)
    {
        const DateTimeStyles utcStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, utcStyles, out utc)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, utcStyles, out utc);
    }

    private static AuthenticationSessionDto ToDto(AuthenticationEvent e) => new(
        e.Id, e.Subject, e.DisplayName, e.StartedAt, e.EndedAt, e.EndReason, e.EndReasonDetail, e.Result,
        e.EndedAt is { } ended ? (long)Math.Max(0, (ended - e.StartedAt).TotalSeconds) : null);
}
