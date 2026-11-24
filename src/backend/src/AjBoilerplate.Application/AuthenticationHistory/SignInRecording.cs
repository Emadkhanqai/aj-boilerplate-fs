using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.AuthenticationHistory;

namespace AjBoilerplate.Application.AuthenticationHistory;

/// <summary>Persistence for <see cref="AuthenticationEvent"/>. Narrow on purpose: the interesting
/// behaviour is the unique-index race on the session id, which the implementation owns.</summary>
public interface IAuthenticationEventRepository
{
    /// <summary>
    /// Saves the sign-in. False when the database already holds that session (an earlier request, a
    /// racing one, or another instance) — the unique index doing its job, not a failure. On any
    /// failure the row is forgotten before the exception propagates, because the context is shared
    /// with the rest of the request and would otherwise send it again.
    /// </summary>
    Task<bool> TryAddAsync(AuthenticationEvent authenticationEvent, CancellationToken cancellationToken);

    /// <summary>One page of sessions, filtered and sorted in the database, with the total before paging.</summary>
    Task<(IReadOnlyList<AuthenticationEvent> Items, int Total)> ListAsync(
        AuthenticationHistoryFilter filter, CancellationToken cancellationToken);

    /// <summary>Every person the history names, once, for the User filter.</summary>
    Task<IReadOnlyList<AuthenticationHistoryUser>> UsersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records how the session ended, once. Atomic in the database (one conditional UPDATE), so two
    /// deliveries racing for the same session cannot both report <see cref="SessionEndWrite.Ended"/>.
    /// </summary>
    Task<SessionEndWrite> EndAsync(
        string sessionId, Guid eventId, string reason, string? reasonDetail, DateTimeOffset endedAt,
        CancellationToken cancellationToken);
}

/// <summary>What <see cref="IAuthenticationEventRepository.EndAsync"/> found.</summary>
public enum SessionEndWrite
{
    /// <summary>This call wrote the ending.</summary>
    Ended,

    /// <summary>The session already has an ending. Nothing written.</summary>
    AlreadyEnded,

    /// <summary>No sign-in was ever recorded for that session id. Nothing written.</summary>
    UnknownSession,
}

/// <summary>Records one sign-in per gateway session.</summary>
public interface ISignInRecorder
{
    /// <summary>
    /// True when this call recorded the session; false when it was already recorded, or when
    /// <paramref name="subject"/> or <paramref name="sessionId"/> is malformed, which records nothing.
    /// </summary>
    Task<bool> RecordAsync(
        string subject, string? displayName, string sessionId, DateTimeOffset startedAt, CancellationToken cancellationToken);
}

public sealed class SignInRecorder : ISignInRecorder
{
    private readonly IAuthenticationEventRepository _repository;
    private readonly IClock _clock;

    public SignInRecorder(IAuthenticationEventRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public Task<bool> RecordAsync(
        string subject, string? displayName, string sessionId, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(subject) || !AuthenticationEvent.IsSessionId(sessionId)
            ? Task.FromResult(false)
            : _repository.TryAddAsync(
                AuthenticationEvent.Record(Guid.NewGuid(), subject, sessionId, startedAt, _clock.UtcNowOffset, displayName),
                cancellationToken);
}
