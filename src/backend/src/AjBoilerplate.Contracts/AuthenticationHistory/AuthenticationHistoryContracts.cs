namespace AjBoilerplate.Contracts.AuthenticationHistory;

/// <summary>One sign-in session in Administration → Sign-in history. One row per session: its ending, when
/// there is one, is on the same row.</summary>
/// <param name="Id">The row's id. Not the gateway session id, which is never sent.</param>
/// <param name="UserId">The identity provider's subject.</param>
/// <param name="UserDisplayName">The name on the token when they signed in; null when it had none.</param>
/// <param name="SignedInAtUtc">When the gateway says the session began.</param>
/// <param name="EndedAtUtc">When the session ended; null while it is open.</param>
/// <param name="EndReason"><c>logged-out</c>, <c>session-expired</c> or <c>signed-out-by-system</c>; null while open.</param>
/// <param name="EndReasonDetail">Which check decided the ending (<c>idle_timeout</c>, …), for investigation; null when unknown.</param>
/// <param name="Result"><c>Failure</c> when the system signed the person out, otherwise <c>Success</c>.</param>
/// <param name="DurationSeconds">Sign-in to ending, whole seconds; null while open.</param>
public sealed record AuthenticationSessionResponse(
    Guid Id,
    string UserId,
    string? UserDisplayName,
    DateTimeOffset SignedInAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? EndReason,
    string? EndReasonDetail,
    string Result,
    long? DurationSeconds);

/// <summary>A person the history names, for the User filter.</summary>
/// <param name="UserId">The identity provider's subject; the value to send as <c>userId</c>.</param>
/// <param name="UserDisplayName">A name recorded for them; null when none ever was.</param>
public sealed record AuthenticationHistoryUserResponse(string UserId, string? UserDisplayName);
