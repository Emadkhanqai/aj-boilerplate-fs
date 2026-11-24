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
public sealed record AuthSession(
    string KeycloakJwt,
    DateTimeOffset JwtExpiresAt,
    string? EntraRefreshToken,
    string Subject,
    string ClientId,
    bool ForceRefresh,
    DateTimeOffset CreatedAt);

/// <summary>A pending sign-in, keyed by the OAuth <c>state</c>. Single use.</summary>
/// <param name="CodeVerifier">PKCE verifier.</param>
/// <param name="ReturnPath">Validated local path to land on afterwards.</param>
/// <param name="BindingHash">SHA-256 of the browser-binding cookie value; ties the callback to the browser
/// that started the flow (login CSRF).</param>
public sealed record LoginState(string CodeVerifier, string ReturnPath, string BindingHash);
