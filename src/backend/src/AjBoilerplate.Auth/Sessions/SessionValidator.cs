using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// <c>/auth/validate</c>: a lock-free read while the JWT is fresh, and a single-flight refresh once it is not, plus the
/// <c>force_refresh</c> convergence of a role change.
/// </summary>
/// <remarks>
/// Only three things end a session: the identity provider's <c>invalid_grant</c> (and its siblings), Keycloak's definitive
/// no-role, and the absolute lifetime. Every infrastructure fault keeps the session and serves the current JWT while it has more
/// than <see cref="AuthSessionOptions.MinimumServableLifetime"/> left, otherwise answers Unavailable. A session with nothing to
/// renew from (no refresh token, or a hand-off whose source session is gone) also ends, but that is decided from stored data,
/// never from a fault.
/// </remarks>
public sealed partial class SessionValidator
{
    private readonly ISessionStore _sessions;
    private readonly IDistributedLock _locks;
    private readonly IEntraClient _entra;
    private readonly IKeycloakExchanger _keycloak;
    private readonly PortalRegistry _portals;
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
        IRevokedSessionMarks revoked,
        PortalRegistry? portals = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions;
        _locks = locks;
        _entra = entra;
        _keycloak = keycloak;
        _portals = portals ?? new PortalRegistry(options);
        _options = options.Value.Session;
        _time = time;
        _logger = logger;
        _revoked = revoked;
    }

    /// <summary>Validates for the single portal of a single-portal installation (a host is then never consulted).</summary>
    public Task<ValidationResult> ValidateAsync(string? sessionId, CancellationToken cancellationToken) =>
        ValidateAsync(sessionId, null, cancellationToken);

    /// <param name="sessionId">The session cookie's value.</param>
    /// <param name="host">The browser-facing host; consulted only when more than one portal is configured.</param>
    public async Task<ValidationResult> ValidateAsync(string? sessionId, string? host, CancellationToken cancellationToken)
    {
        if (!SessionIds.IsWellFormed(sessionId))
        {
            return ValidationResult.Unauthenticated;
        }

        var portal = _portals.Resolve(host);
        if (portal is null)
        {
            LogUnknownHost(_logger);
            return ValidationResult.Unauthenticated;
        }

        var key = SessionIds.StorageKey(sessionId!);
        var session = await _sessions.GetAsync(key, cancellationToken);
        if (session is null)
        {
            return await GoneAsync(key, cancellationToken);
        }

        // Host-only cookies make a cross-portal session impossible in a browser; refuse one anyway.
        if (!string.Equals(session.ClientId, portal.ClientId, StringComparison.Ordinal))
        {
            return ValidationResult.Unauthenticated;
        }

        // The sign-in time this session is bounded by, and the one /auth/validate reports to the
        // portal (X-Usp-Session-Started-At). Fixed for the life of the session: nothing after the
        // stamp below ever rewrites it, so it is read once here and carried to every Ok.
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

        return await RefreshAsync(key, session, portal, signedInAt.Value, cancellationToken);
    }

    /// <summary>
    /// The absolute lifetime. A POLICY end, not an infrastructure one: it is decided from the stored
    /// sign-in time alone, so no fault can trigger it. The session is deleted; a delete that fails
    /// is logged and the answer is still 401, because the next validate re-reaches the same verdict.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the session has outlived its absolute lifetime (and is ended); otherwise the
    /// sign-in time that bounds it.
    /// </returns>
    private async Task<DateTimeOffset?> SignedInAtUnlessExpiredAsync(string key, AuthSession session, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (session.CreatedAt <= DateTimeOffset.UnixEpoch)
        {
            // No sign-in time recorded: start the clock now rather than end the session (the
            // ruling's "existing sessions must not be ended at deploy").
            await _sessions.StampSignInTimeAsync(key, now, cancellationToken);
            LogSignInTimeStamped(_logger);

            // The stamp is set-if-absent, so two first validates racing each other both stamp and
            // only one value survives. Report the one that survived, not this request's own `now`,
            // or the portal is told two different sign-in times for one session.
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
            // Reported as having ended AT the deadline, not when this request noticed it: the same
            // time the sweeper would report for a session that reached it with nobody watching.
            await _sessions.EndAsync(
                key,
                session.Subject,
                SessionEnd.Expired(SessionEndDetails.AbsoluteLifetime, session.CreatedAt + _options.AbsoluteLifetime),
                cancellationToken);
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
        string key, AuthSession observed, PortalOptions portal, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        // The lock is per GRANT, not per session (docs/auth/handoff.md): a handed-off
        // session renews from the refresh token of the session named by RefreshVia, so it must
        // serialise against THAT session's refreshes, or the two would present one rotating token
        // in parallel and one of them would be handed invalid_grant.
        var lineageKey = observed.RefreshVia ?? key;
        var handle = await _locks.TryAcquireAsync(LockKey(lineageKey), _options.RefreshLockLifetime, cancellationToken);
        var contended = false;
        if (handle is null)
        {
            var wait = await AwaitWinnerAsync(key, lineageKey, observed, signedInAt, cancellationToken);
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

            // Where the refresh token lives: in this session, or in its lineage root.
            AuthSession? root = null;
            if (session.RefreshVia is not null)
            {
                root = await _sessions.GetAsync(session.RefreshVia, cancellationToken);
                if (root is null || !string.Equals(root.Subject, session.Subject, StringComparison.Ordinal))
                {
                    // The session this one borrowed its grant from has been signed out, swept or
                    // ended: there is nothing left to renew from, which is the same end state as
                    // a session Entra issued no refresh token to (below). Decided from stored data,
                    // so no infrastructure fault reaches it -- a store fault throws instead.
                    LogSessionEnded(_logger, SessionEndDetails.RefreshLineageEnded);
                    await EndBySystemAsync(key, session, SessionEndDetails.RefreshLineageEnded, cancellationToken);
                    return ValidationResult.Unauthenticated;
                }
            }

            var heldToken = root is null ? session.EntraRefreshToken : root.EntraRefreshToken;
            if (string.IsNullOrEmpty(heldToken))
            {
                // Entra issued no refresh token (offline_access not consented): the session cannot be
                // renewed, which is the same end state as invalid_grant.
                LogNoRefreshToken(_logger);
                await EndBySystemAsync(key, session, SessionEndDetails.NoRefreshToken, cancellationToken);
                return ValidationResult.Unauthenticated;
            }

            var entra = await _entra.RefreshAsync(heldToken, cancellationToken);
            switch (entra.Outcome)
            {
                case EntraOutcome.InvalidGrant:
                    // `invalid_grant` is one of the only two verdicts allowed to end a session --
                    // but only when it is a verdict ABOUT THE USER. Entra rotates refresh tokens on
                    // use, so a refresh running without a valid lock can be handed the same
                    // `invalid_grant` simply because another refresher already consumed the token it
                    // presented. The two are indistinguishable in the response, and the refresh path
                    // CAN outlive its lock: the lock is 5s on the defaults while Entra's timeout
                    // plus the Keycloak retry ladder is ~25s (see SessionOptions.RefreshLockLifetime).
                    //
                    // So the lock is the evidence. Held -> we are the only refresher, the verdict is
                    // about the user, end the session. Lapsed -> we cannot attribute it, and
                    // "an infrastructure fault must never sign anyone out" decides the tie: keep the
                    // session and serve or 503. The grant, if it really is dead, is re-tested by the
                    // next request under a fresh lock, which answers definitively.
                    if (_time.GetUtcNow() - acquiredAt >= _options.RefreshLockLifetime)
                    {
                        LogRefreshOutlivedLock(_logger, "entra_invalid_grant_unattributable", (long)_options.RefreshLockLifetime.TotalMilliseconds);
                        return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
                    }

                    // A lock acquired only AFTER waiting may be one that LAPSED while its previous holder was
                    // still in flight with the same token, so the same tie-break applies.
                    if (contended)
                    {
                        LogInvalidGrantAfterWait(_logger, "/auth/validate");
                        return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
                    }

                    LogSessionEnded(_logger, SessionEndDetails.EntraInvalidGrant);
                    await EndBySystemAsync(key, session, SessionEndDetails.EntraInvalidGrant, cancellationToken);
                    return ValidationResult.Unauthenticated;

                case EntraOutcome.Transient:
                    LogRefreshDeferred(_logger, "entra");
                    return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
            }

            // Entra rotates refresh tokens: from here on the NEW one must survive whatever Keycloak says.
            var refreshToken = entra.RefreshToken ?? heldToken;

            // A borrowed grant is written back to its OWNER at once, before Keycloak is asked, for
            // the same reason: the owner and every other session of the lineage renew from it next.
            if (session.RefreshVia is not null && !string.Equals(refreshToken, heldToken, StringComparison.Ordinal))
            {
                await _sessions.UpdateRefreshTokenAsync(session.RefreshVia, refreshToken, cancellationToken);
            }

            var exchange = await _keycloak.ExchangeAsync(entra.AccessToken!, portal, cancellationToken);
            switch (exchange.Outcome)
            {
                case ExchangeOutcome.Denied:
                    LogSessionEnded(_logger, SessionEndDetails.KeycloakDenied);
                    await MarkRevokedAsync(key, cancellationToken);
                    await EndBySystemAsync(key, session, SessionEndDetails.KeycloakDenied, cancellationToken);
                    return ValidationResult.Forbidden;

                case ExchangeOutcome.Transient:
                    LogRefreshDeferred(_logger, "keycloak");
                    if (session.RefreshVia is null && !string.Equals(refreshToken, session.EntraRefreshToken, StringComparison.Ordinal))
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
                if (session.RefreshVia is null)
                {
                    await _sessions.UpdateRefreshTokenAsync(key, refreshToken, cancellationToken);
                }

                return await ServeCurrentAsync(key, session, signedInAt, cancellationToken);
            }

            if (claims.ClientRoles(portal.ClientId).Count == 0)
            {
                LogSessionEnded(_logger, SessionEndDetails.NoClientRole);
                await MarkRevokedAsync(key, cancellationToken);
                await EndBySystemAsync(key, session, SessionEndDetails.NoClientRole, cancellationToken);
                return ValidationResult.Forbidden;
            }

            // A derived session never holds a refresh token of its own: null keeps its field empty.
            await _sessions.UpdateTokensAsync(key, exchange.Jwt!, claims.ExpiresAt, session.RefreshVia is null ? refreshToken : null, cancellationToken);
            await _sessions.TouchAsync(key, session.Subject, _options.IdleTimeout, cancellationToken);
            if (root is not null)
            {
                // Activity on a derived session is activity on the grant: without this, a day spent
                // in the portal handed off to would let the idle source session expire under it.
                await _sessions.TouchAsync(session.RefreshVia!, root.Subject, _options.IdleTimeout, cancellationToken);
            }

            return ValidationResult.Ok(exchange.Jwt!, key, signedInAt);
        }
    }

    /// <summary>Lost the lock: poll for the winner's result instead of refreshing in parallel —
    /// with refresh-token rotation, parallel refreshes would all but one fail <c>invalid_grant</c>.</summary>
    /// <remarks>
    /// <para>
    /// The budget is <see cref="AuthSessionOptions.RefreshWaitBudget"/>, which is derived from the lock
    /// lifetime rather than configured beside it: a separately configured wait shorter than the winner's lock gives up while a
    /// refresh is still legitimately in progress, and the user gets a 503.
    /// </para>
    /// <para>
    /// <b>It also re-tries the lock on every poll</b>, and returns the handle when
    /// it gets one. The lock is per grant (docs/auth/handoff.md), so the winner may be
    /// a DIFFERENT session of the same lineage, whose refresh never makes this session's JWT fresh;
    /// waiting for this session to change would wait out the whole budget every time. A refresh run
    /// on a handle got this way never ends a session on <c>invalid_grant</c> (see RefreshAsync).
    /// </para>
    /// </remarks>
    private async Task<(ValidationResult? Result, IAsyncDisposable? Handle)> AwaitWinnerAsync(
        string key, string lineageKey, AuthSession observed, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        var current = observed;
        var attempts = _options.RefreshWaitAttempts;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(_options.LockPollInterval, _time, cancellationToken);

            var reread = await _sessions.GetAsync(key, cancellationToken);
            if (reread is null)
            {
                // The winner ended the session (invalid_grant / no role). A no-role end is reported as such, so this request
                // agrees with the one that ended it.
                return (await GoneAsync(key, cancellationToken), null);
            }

            current = reread;
            if (!NeedsRefresh(current))
            {
                return (ValidationResult.Ok(current.KeycloakJwt, key, signedInAt), null);
            }

            var handle = await _locks.TryAcquireAsync(LockKey(lineageKey), _options.RefreshLockLifetime, cancellationToken);
            if (handle is not null)
            {
                return (null, handle);
            }
        }

        // Winner still busy (or it deferred on a transient fault). Not a reason to log anyone out:
        // serve the current token while valid, otherwise report unavailability, never 401.
        var servable = IsServable(current);

        // THE ONLY RECORD THAT THIS PATH RAN. Without it, a user shown nginx's branded
        // "Temporarily unavailable" page produced no line in this service naming a reason, and the
        // 503 was indistinguishable in the request log from a crash, a cold start or a bad gateway
        // hop. `Servable` is what separates the two outcomes of the same give-up, and
        // `RemainingMs` is why -- it is the quantity MinimumServableLifetime is compared against.
        //
        // Field names avoid "jwt" and "token": a log pipeline that redacts by NAME would otherwise blank a duration.
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
    /// The answer for a cookie whose session does not exist: 403 (access revoked) when it was ended
    /// for a definitive no-role within <see cref="AuthSessionOptions.RevokedMarkerLifetime"/>, otherwise
    /// 401 as always (docs/auth/gateway-contract.md). A marks store that cannot answer means 401 --
    /// today's behaviour -- so a fault can never produce the revoked signal.
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

    /// <summary>
    /// Written BEFORE the session is ended, so a request polling for this refresh finds the mark the
    /// moment it finds the session gone. A failure is logged and ignored: the session still ends
    /// and this request still answers 403; only later requests fall back to 401.
    /// </summary>
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

    /// <summary>
    /// A definitive verdict about the user, decided from stored data or from the identity
    /// providers' own answer: "Signed out by system" in the portal's history (ADR 0004). Never
    /// reached by an infrastructure fault -- those keep the session and produce no event.
    /// </summary>
    private Task<bool> EndBySystemAsync(string key, AuthSession session, string detail, CancellationToken cancellationToken) =>
        _sessions.EndAsync(key, session.Subject, SessionEnd.BySystem(detail, _time.GetUtcNow()), cancellationToken);

    internal static string LockKey(string sessionKey) => "refresh:" + sessionKey;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoked-session mark {Operation} failed ({ExceptionType}); a gone session answers 401, as it did before the mark existed.")]
    private static partial void LogRevokedMarkUnavailable(ILogger logger, string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validate called for a host that is not a configured portal.")]
    private static partial void LogUnknownHost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session ended: {Reason}.")]
    private static partial void LogSessionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh deferred after a transient {Provider} failure; current session kept.")]
    private static partial void LogRefreshDeferred(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session has no Entra refresh token; it cannot be renewed and is ended.")]
    private static partial void LogNoRefreshToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak returned a token without a readable sub/exp payload.")]
    private static partial void LogUnreadableJwt(ILogger logger);

    // {Endpoint} is a PARAMETER, not text in the template: a surface baked into a shared template becomes a false citation
    // the day a second caller uses it.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh single-flight wait expired on {Endpoint} after {BudgetMs}ms ({Attempts} polls): the lock holder had not published a renewed session. Outcome {Outcome}; servable={Servable}, {RemainingMs}ms of validity left.")]
    private static partial void LogRefreshWaitExpired(
        ILogger logger, string endpoint, long budgetMs, int attempts, long remainingMs, bool servable, string outcome);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh outlived its own {LockMs}ms lock, so an invalid_grant cannot be attributed to the user rather than to a concurrent refresher: session KEPT ({Reason}).")]
    private static partial void LogRefreshOutlivedLock(ILogger logger, string reason, long lockMs);

    // {Endpoint} is a parameter for the same reason as LogRefreshWaitExpired's.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refresh on {Endpoint} acquired the lock only after waiting for another holder, so an invalid_grant cannot be attributed to the user rather than to that holder: session KEPT.")]
    private static partial void LogInvalidGrantAfterWait(ILogger logger, string endpoint);

    // Fixed text, no subject, no session id: the event is the fact.
    [LoggerMessage(Level = LogLevel.Information, Message = "Session reached its absolute lifetime of {LifetimeHours}h since sign-in: ended, 401.")]
    private static partial void LogAbsoluteLifetimeReached(ILogger logger, double lifetimeHours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session past its absolute lifetime could not be deleted ({ExceptionType}); answered 401 regardless, and the next validate will reach the same verdict.")]
    private static partial void LogAbsoluteLifetimeDeleteFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session had no recorded sign-in time: its absolute lifetime starts now.")]
    private static partial void LogSignInTimeStamped(ILogger logger);
}
