namespace AjBoilerplate.Auth.Sessions;

/// <summary>Server-side session state. The browser only ever holds the opaque session id.</summary>
/// <param name="KeycloakJwt">The Keycloak access token nginx injects as <c>Authorization</c>.</param>
/// <param name="JwtExpiresAt">Its <c>exp</c>.</param>
/// <param name="EntraRefreshToken">Rotating refresh token of the identity provider; <c>null</c> when none was issued.</param>
/// <param name="Subject">Keycloak <c>sub</c>: the key the admin force-refresh arrives with.</param>
/// <param name="ClientId">Keycloak client the JWT was minted for.</param>
/// <param name="ForceRefresh">Set by the admin endpoint; the next request refreshes regardless of expiry.</param>
/// <param name="CreatedAt">Sign-in time. <see cref="Configuration.AuthSessionOptions.AbsoluteLifetime"/> after it, validation
/// answers 401. A value at or before the Unix epoch means "not recorded" and is stamped on first sight.</param>
/// <param name="RefreshVia">Storage key of the session whose identity-provider refresh token this one renews from, or <c>null</c>
/// when it holds its own (every session signed in through the identity provider). Set only by a portal hand-off: a refresh token
/// rotates on use, so one grant cannot back two independent sessions, and a handed-off session borrows the grant of the session it
/// came from, under THAT session's refresh lock. Such a session's <see cref="EntraRefreshToken"/> is always <c>null</c>.</param>
public sealed record AuthSession(
    string KeycloakJwt,
    DateTimeOffset JwtExpiresAt,
    string? EntraRefreshToken,
    string Subject,
    string ClientId,
    bool ForceRefresh,
    DateTimeOffset CreatedAt,
    string? RefreshVia = null);

/// <summary>A pending sign-in, keyed by the OAuth <c>state</c>. Single use.</summary>
/// <param name="CodeVerifier">PKCE verifier.</param>
/// <param name="ReturnPath">Validated local path to land on afterwards.</param>
/// <param name="BindingHash">SHA-256 of the browser-binding cookie value; ties the callback to the browser
/// that started the flow (login CSRF).</param>
/// <param name="Host">Host of the portal the flow started on; <c>null</c> for a state written before portals could differ. The
/// callback must arrive on the same portal.</param>
public sealed record LoginState(string CodeVerifier, string ReturnPath, string BindingHash, string? Host = null);

/// <summary>
/// A one-time portal hand-off ticket, keyed by the SHA-256 of the ticket value. Single use, short lived. It names a SESSION, not a
/// person: the subject is carried only so redeem can check that the session still belongs to the same one.
/// </summary>
/// <param name="SourceSessionKey">Storage key of the session the hand-off started from.</param>
/// <param name="Subject">That session's subject when the ticket was minted.</param>
/// <param name="SourceClientId">Client id of the portal the hand-off started on.</param>
/// <param name="SourceOrigin">Browser origin of that portal; redeem requires this <c>Origin</c>.</param>
/// <param name="TargetClientId">The only portal that may redeem the ticket.</param>
/// <param name="ReturnPath">Validated local path to land on, on the target portal.</param>
public sealed record HandoffTicket(
    string SourceSessionKey, string Subject, string SourceClientId, string SourceOrigin, string TargetClientId, string ReturnPath);
