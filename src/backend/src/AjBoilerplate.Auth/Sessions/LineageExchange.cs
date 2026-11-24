using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// Mints a Keycloak token for ANOTHER portal's client from a session's Entra grant, under that
/// grant's refresh lock. Kept apart from <see cref="HandoffService.RedeemAsync"/> so the rotation rules live in one
/// place for whatever mints a token for another portal from an existing grant.
/// </summary>
/// <remarks>
/// <b>Nothing here ends a session, and nothing here logs.</b> Every outcome comes back as a reason
/// slug for the caller to log under its own surface. Entra <c>invalid_grant</c> is a refusal, not a
/// session end, even under a held lock: the lineage root's own next validate re-tests the grant and is
/// the one entitled to end it. A no-role is a verdict about the TARGET portal only.
/// </remarks>
internal sealed class LineageExchange(
    ISessionStore sessions,
    IDistributedLock locks,
    IEntraClient entra,
    IKeycloakExchanger keycloak,
    AuthSessionOptions options,
    TimeProvider time)
{
    /// <summary>The lineage root's refresh lock, waited for within the same budget a validate waits.</summary>
    public async Task<IAsyncDisposable?> AcquireAsync(string rootKey, CancellationToken cancellationToken)
    {
        var lockKey = SessionValidator.LockKey(rootKey);
        var handle = await locks.TryAcquireAsync(lockKey, options.RefreshLockLifetime, cancellationToken);
        for (var attempt = 0; handle is null && attempt < options.RefreshWaitAttempts; attempt++)
        {
            await Task.Delay(options.LockPollInterval, time, cancellationToken);
            handle = await locks.TryAcquireAsync(lockKey, options.RefreshLockLifetime, cancellationToken);
        }

        return handle;
    }

    /// <summary>Call only while holding the handle <see cref="AcquireAsync"/> returned for <paramref name="rootKey"/>.</summary>
    public async Task<LineageMint> MintUnderLockAsync(string rootKey, string subject, PortalOptions portal, CancellationToken cancellationToken)
    {
        // Re-read under the lock: a refresh of the lineage may have rotated the token since.
        var root = await sessions.GetAsync(rootKey, cancellationToken);
        if (root is null || !string.Equals(root.Subject, subject, StringComparison.Ordinal))
        {
            return LineageMint.Refused("lineage_root_gone");
        }

        if (string.IsNullOrEmpty(root.EntraRefreshToken))
        {
            return LineageMint.Refused("no_refresh_token");
        }

        var refreshed = await entra.RefreshAsync(root.EntraRefreshToken, cancellationToken);
        switch (refreshed.Outcome)
        {
            case EntraOutcome.InvalidGrant:
                return LineageMint.Refused("entra_invalid_grant");
            case EntraOutcome.Transient:
                return LineageMint.Refused("entra_transient");
        }

        // Rotated: written back to its owner before Keycloak is asked (a rotated refresh token must survive whatever Keycloak says).
        var rotated = refreshed.RefreshToken ?? root.EntraRefreshToken;
        if (!string.Equals(rotated, root.EntraRefreshToken, StringComparison.Ordinal))
        {
            await sessions.UpdateRefreshTokenAsync(rootKey, rotated, cancellationToken);
        }

        var exchange = await keycloak.ExchangeAsync(refreshed.AccessToken!, portal, cancellationToken);
        switch (exchange.Outcome)
        {
            case ExchangeOutcome.Denied:
                return LineageMint.NoAccess("keycloak_denied");
            case ExchangeOutcome.Transient:
                return LineageMint.Refused("keycloak_transient");
        }

        var claims = JwtClaims.TryParse(exchange.Jwt);
        if (claims is null)
        {
            return LineageMint.Refused("keycloak_unreadable_jwt");
        }

        // The same two refusals as the sign-in callback, in the same order and with the same
        // meaning (SignInService.CompleteAsync).
        if (claims.IsForAnotherClient(portal.ClientId))
        {
            return LineageMint.NoAccess("token_for_another_client");
        }

        if (claims.ClientRoles(portal.ClientId).Count == 0)
        {
            return LineageMint.NoAccess("no_client_role");
        }

        // The minted token is about whoever Entra says the grant belongs to. It must be the
        // session's own person; anything else is a fault somewhere upstream, never a grant.
        if (!string.Equals(claims.Subject, subject, StringComparison.Ordinal))
        {
            return LineageMint.Refused(LineageMint.SubjectMismatch);
        }

        return new LineageMint(LineageMintStatus.Minted, "minted", exchange.Jwt, claims);
    }
}

internal enum LineageMintStatus
{
    /// <summary>A token for the target client, about the session's own subject, with at least one role.</summary>
    Minted,

    /// <summary>A definitive verdict that the user has no access to the TARGET portal.</summary>
    NoAccess,

    /// <summary>Anything else: a fault or a state that is not a verdict about the user.</summary>
    Refused,
}

/// <param name="Status">The outcome.</param>
/// <param name="Reason">A slug naming the check or upstream that decided it; safe to log.</param>
/// <param name="Jwt">The minted token, only on <see cref="LineageMintStatus.Minted"/>. Never logged.</param>
/// <param name="Claims">Its parsed claims, only on <see cref="LineageMintStatus.Minted"/>.</param>
internal sealed record LineageMint(LineageMintStatus Status, string Reason, string? Jwt = null, JwtClaims? Claims = null)
{
    public const string SubjectMismatch = "minted_subject_mismatch";

    public static LineageMint Refused(string reason) => new(LineageMintStatus.Refused, reason);

    public static LineageMint NoAccess(string reason) => new(LineageMintStatus.NoAccess, reason);
}
