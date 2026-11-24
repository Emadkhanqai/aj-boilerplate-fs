using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>Why a session ended. Logged as a reason slug; never carries a subject, token or session id.</summary>
public static class SessionEndDetails
{
    public const string AbsoluteLifetime = "absolute_lifetime";
    public const string EntraInvalidGrant = "entra_invalid_grant";
    public const string KeycloakDenied = "keycloak_denied";
    public const string NoClientRole = "no_client_role";
    public const string NoRefreshToken = "no_refresh_token";
}

/// <summary>
/// <c>/auth/validate</c>: a lock-free read while the JWT is fresh, and a single-flight refresh once it is not.
/// </summary>
/// <remarks>
/// Only three things end a session: the identity provider's <c>invalid_grant</c> (and its siblings),
/// Keycloak's definitive no-role, and the absolute lifetime. Every infrastructure fault keeps the
/// session and serves the current JWT while it has more than <see cref="AuthSessionOptions.MinimumServableLifetime"/>
/// left, otherwise answers Unavailable. A session with nothing to renew from (no refresh token) also
/// ends, but that is decided from stored data, never from a fault.
/// </remarks>
public sealed partial class SessionValidator
{
    private readonly ISessionStore _sessions;
    private readonly IDistributedLock _locks;
    private readonly IEntraClient _entra;
    private readonly IKeycloakExchanger _keycloak;
    private readonly PortalOptions _portal;
    private readonly AuthSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionValidator> _logger;
    private readonly IRevokedSessionMarks _revoked;

    public SessionValidator(
        ISessionStore sessions,
        IDistributedLock locks,
        IEntraClient entra,
        IKeycloakExchanger keycloak,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILogger<SessionValidator> logger,
        IRevokedSessionMarks revoked)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions;
        _locks = locks;
        _entra = entra;
        _keycloak = keycloak;
        _portal = options.Value.Portal;
        _options = options.Value.Session;
        _time = time;
        _logger = logger;
        _revoked = revoked;
    }

    public async Task<ValidationResult> ValidateAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!SessionIds.IsWellFormed(sessionId))
        {
            return ValidationResult.Unauthenticated;
        }

        var key = SessionIds.StorageKey(sessionId!);
        var session = await _sessions.GetAsync(key, cancellationToken);
        if (session is null)
        {
            return await GoneAsync(key, cancellationToken);
        }

        var signedInAt = await SignedInAtUnlessExpiredAsync(key, session, cancellationToken);
        if (signedInAt is null)
        {
            return ValidationResult.Unauthenticated;
        }

        if (!NeedsRefresh(session))
        {
            await _sessions.TouchAsync(key, session.Subject, _options.IdleTimeout, cancellationToken);
            return ValidationResult.Ok(session.KeycloakJwt, key, signedInAt.Value);
        }

        return await RefreshAsync(key, session, signedInAt.Value, cancellationToken);
    }

    /// <summary>
    /// The absolute lifetime. A POLICY end decided from the stored sign-in time alone, so no fault can trigger it.
    /// A delete that fails is logged and the answer is still 401: the next validate reaches the same verdict.
    /// </summary>
    /// <returns><c>null</c> when the session has outlived its lifetime (and is ended); otherwise its sign-in time.</returns>
    private async Task<DateTimeOffset?> SignedInAtUnlessExpiredAsync(string key, AuthSession session, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (session.CreatedAt <= DateTimeOffset.UnixEpoch)
        {
            // No sign-in time recorded: start the clock now rather than end the session.
            await _sessions.StampSignInTimeAsync(key, now, cancellationToken);
            LogSignInTimeStamped(_logger);

            // The stamp is set-if-absent: report the value that survived a race, not this request's own `now`.
            var stamped = await _sessions.GetAsync(key, cancellationToken);
            return stamped is not null && stamped.CreatedAt > DateTimeOffset.UnixEpoch ? stamped.CreatedAt : now;
        }

        if (now - session.CreatedAt < _options.AbsoluteLifetime)
        {
            return session.CreatedAt;
        }

        LogAbsoluteLifetimeReached(_logger, _options.AbsoluteLifetime.TotalHours);
        try
        {
            await _sessions.EndAsync(key, session.Subject, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAbsoluteLifetimeDeleteFailed(_logger, ex.GetType().Name);
        }

        return null;
    }

    internal bool NeedsRefresh(AuthSession session) =>
        session.ForceRefresh || session.JwtExpiresAt - _time.GetUtcNow() <= _options.RefreshSkew;

    private bool IsServable(AuthSession session) =>
        session.JwtExpiresAt - _time.GetUtcNow() > _options.MinimumServableLifetime;

    private async Task<ValidationResult> RefreshAsync(
        string key, AuthSession observed, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        var handle = await _locks.TryAcquireAsync(LockKey(key), _options.RefreshLockLifetime, cancellationToken);
        var contended = false;
        if (handle is null)
        {
            var wait = await AwaitWinnerAsync(key, observed, signedInAt, cancellationToken);
            if (wait.Result is not null)
            {
                return wait.Result;
            }

            handle = wait.Handle!;
            contended = true;
        }

        var acquiredAt = _time.GetUtcNow();

        await using (handle)
        {
            // Re-read under the lock: a refresh may have completed between our read and the acquire.
            var session = await _sessions.GetAsync(key, cancellationToken);
            if (session is null)
            {
                return await GoneAsync(key, cancellationToken);
            }

            if (!NeedsRefresh(session))
            {
                await _sessions.TouchAsync(key, session.Subject, _options.IdleTimeout, cancellationToken);
                return ValidationResult.Ok(session.KeycloakJwt, key, signedInAt);
            }

            var heldToken = session.EntraRefreshToken;
            if (string.IsNullOrEmpty(heldToken))
            {
                // No refresh token (offline_access not consented): cannot be renewed, same end state as invalid_grant.
                LogNoRefreshToken(_logger);
                await EndAsync(key, session, cancellationToken);
                return ValidationResult.Unauthenticated;
            }

            var entra = await _entra.RefreshAsync(heldToken, cancellationToken);
            switch (entra.Outcome)
            {
                case EntraOutcome.InvalidGrant:
                    // invalid_grant ends a session only when it is a verdict ABOUT THE USER. Refresh tokens
                    // rotate on use, so a refresher running without a valid lock can be handed the same
                    // invalid_grant because another refresher already consumed its token. The lock is the
                    // evidence: held for the whole call, we are the only refresher; lapsed (or acquired only
                    // after waiting), we cannot attribute it, and "a fault never signs anyone out" breaks the tie.
                    if (_time.GetUtcNow() - acquiredAt >= _options.RefreshLockLifetime)
                    {
                        LogRefreshOutlivedLock(_logger, "entra_invalid_grant_unattributable", (long)_options.RefreshLockLifetime.TotalMilliseconds);
                        return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
                    }

                    if (contended)
                    {
                        LogInvalidGrantAfterWait(_logger, "/auth/validate");
                        return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
                    }

                    LogSessionEnded(_logger, SessionEndDetails.EntraInvalidGrant);
                    await EndAsync(key, session, cancellationToken);
                    return ValidationResult.Unauthenticated;

                case EntraOutcome.Transient:
                    LogRefreshDeferred(_logger, "entra");
                    return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
            }

            // The identity provider rotates refresh tokens: the NEW one must survive whatever Keycloak says.
            var refreshToken = entra.RefreshToken ?? heldToken;

            var exchange = await _keycloak.ExchangeAsync(entra.AccessToken!, _portal, cancellationToken);
            switch (exchange.Outcome)
            {
                case ExchangeOutcome.Denied:
                    LogSessionEnded(_logger, SessionEndDetails.KeycloakDenied);
                    await MarkRevokedAsync(key, cancellationToken);
                    await EndAsync(key, session, cancellationToken);
                    return ValidationResult.Forbidden;

                case ExchangeOutcome.Transient:
                    LogRefreshDeferred(_logger, "keycloak");
                    if (!string.Equals(refreshToken, session.EntraRefreshToken, StringComparison.Ordinal))
                    {
                        await _sessions.UpdateRefreshTokenAsync(key, refreshToken, cancellationToken);
                    }

                    return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
            }

            var claims = JwtClaims.TryParse(exchange.Jwt);
            if (claims is null)
            {
                // A 200 we cannot read is Keycloak misbehaving, not a verdict about the user.
                LogUnreadableJwt(_logger);
                await _sessions.UpdateRefreshTokenAsync(key, refreshToken, cancellationToken);
                return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
            }

            if (claims.ClientRoles(_portal.ClientId).Count == 0)
            {
                LogSessionEnded(_logger, SessionEndDetails.NoClientRole);
                await MarkRevokedAsync(key, cancellationToken);
                await EndAsync(key, session, cancellationToken);
                return ValidationResult.Forbidden;
            }

            await _sessions.UpdateTokensAsync(key, exchange.Jwt!, claims.ExpiresAt, refreshToken, cancellationToken);
            await _sessions.TouchAsync(key, session.Subject, _options.IdleTimeout, cancellationToken);
            return ValidationResult.Ok(exchange.Jwt!, key, signedInAt);
        }
    }

    /// <summary>
    /// Lost the lock: poll for the winner's result instead of refreshing in parallel (parallel refreshes
    /// with a rotating token would all but one fail invalid_grant). Each poll also re-tries the lock, so a
    /// winner that died without publishing does not leave the session unrefreshable until the budget runs out.
    /// </summary>
    private async Task<(ValidationResult? Result, IAsyncDisposable? Handle)> AwaitWinnerAsync(
        string key, AuthSession observed, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        var current = observed;
        var attempts = _options.RefreshWaitAttempts;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(_options.LockPollInterval, _time, cancellationToken);

            var reread = await _sessions.GetAsync(key, cancellationToken);
            if (reread is null)
            {
                return (await GoneAsync(key, cancellationToken), null);
            }

            current = reread;
            if (!NeedsRefresh(current))
            {
                return (ValidationResult.Ok(current.KeycloakJwt, key, signedInAt), null);
            }

            var handle = await _locks.TryAcquireAsync(LockKey(key), _options.RefreshLockLifetime, cancellationToken);
            if (handle is not null)
            {
                return (null, handle);
            }
        }

        // Winner still busy (or deferred on a transient fault): serve the current token while valid, otherwise 503, never 401.
        var servable = IsServable(current);
        LogRefreshWaitExpired(
            _logger,
            "/auth/validate",
            (long)_options.RefreshWaitBudget.TotalMilliseconds,
            attempts,
            (long)(current.JwtExpiresAt - _time.GetUtcNow()).TotalMilliseconds,
            servable,
            servable ? "serving_current" : "unavailable_503");

        return (servable ? ValidationResult.Ok(current.KeycloakJwt, key, signedInAt) : ValidationResult.Unavailable, null);
    }

    /// <summary>
    /// The answer for a cookie whose session does not exist: 403 (access revoked) when it was ended for a
    /// definitive no-role within <see cref="AuthSessionOptions.RevokedMarkerLifetime"/>, otherwise 401. A marks
    /// store that cannot answer means 401, so a fault can never produce the revoked signal.
    /// </summary>
    private async Task<ValidationResult> GoneAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _revoked.IsMarkedAsync(key, cancellationToken) ? ValidationResult.Forbidden : ValidationResult.Unauthenticated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRevokedMarkUnavailable(_logger, "read", ex.GetType().Name);
            return ValidationResult.Unauthenticated;
        }
    }

    /// <summary>Written BEFORE the session is ended so a request polling for this refresh finds the mark the moment the session is gone. A failure is logged and ignored.</summary>
    private async Task MarkRevokedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _revoked.MarkAsync(key, _options.RevokedMarkerLifetime, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRevokedMarkUnavailable(_logger, "write", ex.GetType().Name);
        }
    }

    private async Task<ValidationResult> ServeCurrentAsync(
        string key, AuthSession session, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        if (!IsServable(session))
        {
            return ValidationResult.Unavailable;
        }

        await _sessions.TouchAsync(key, session.Subject, _options.IdleTimeout, cancellationToken);
        return ValidationResult.Ok(session.KeycloakJwt, key, signedInAt);
    }

    private Task<bool> EndAsync(string key, AuthSession session, CancellationToken cancellationToken) =>
        _sessions.EndAsync(key, session.Subject, cancellationToken);

    internal static string LockKey(string sessionKey) => "refresh:" + sessionKey;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoked-session mark {Operation} failed ({ExceptionType}); a gone session answers 401.")]
    private static partial void LogRevokedMarkUnavailable(ILogger logger, string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session ended: {Reason}.")]
    private static partial void LogSessionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh deferred after a transient {Provider} failure; current session kept.")]
    private static partial void LogRefreshDeferred(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session has no refresh token; it cannot be renewed and is ended.")]
    private static partial void LogNoRefreshToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak returned a token without a readable sub/exp payload.")]
    private static partial void LogUnreadableJwt(ILogger logger);

    // {Endpoint} is a PARAMETER, never text in the template: a surface baked into a shared template becomes a false citation.
    // Field names avoid "jwt"/"token": log redaction by NAME would otherwise blank a duration.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh single-flight wait expired on {Endpoint} after {BudgetMs}ms ({Attempts} polls): the lock holder had not published a renewed session. Outcome {Outcome}; servable={Servable}, {RemainingMs}ms of validity left.")]
    private static partial void LogRefreshWaitExpired(
        ILogger logger, string endpoint, long budgetMs, int attempts, long remainingMs, bool servable, string outcome);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh outlived its own {LockMs}ms lock, so an invalid_grant cannot be attributed to the user rather than to a concurrent refresher: session KEPT ({Reason}).")]
    private static partial void LogRefreshOutlivedLock(ILogger logger, string reason, long lockMs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh on {Endpoint} acquired the lock only after waiting for another holder, so an invalid_grant cannot be attributed to the user rather than to that holder: session KEPT.")]
    private static partial void LogInvalidGrantAfterWait(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session reached its absolute lifetime of {LifetimeHours}h since sign-in: ended, 401.")]
    private static partial void LogAbsoluteLifetimeReached(ILogger logger, double lifetimeHours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session past its absolute lifetime could not be deleted ({ExceptionType}); answered 401 regardless, and the next validate reaches the same verdict.")]
    private static partial void LogAbsoluteLifetimeDeleteFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session had no recorded sign-in time: its absolute lifetime starts now.")]
    private static partial void LogSignInTimeStamped(ILogger logger);
}
