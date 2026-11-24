using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;

namespace AjBoilerplate.Auth.Abstractions;

public enum EntraOutcome
{
    Success,

    /// <summary><c>invalid_grant</c>, <c>interaction_required</c>, <c>consent_required</c> or <c>login_required</c>:
    /// the grant is dead. One of the conditions that end a session.</summary>
    InvalidGrant,

    /// <summary>Network, timeout, 5xx, throttling or our own misconfiguration. Never ends a session.</summary>
    Transient,
}

public sealed record EntraTokenResult(EntraOutcome Outcome, string? AccessToken = null, string? RefreshToken = null)
{
    public static EntraTokenResult InvalidGrant { get; } = new(EntraOutcome.InvalidGrant);

    public static EntraTokenResult Transient { get; } = new(EntraOutcome.Transient);
}

/// <summary>The upstream OIDC identity provider (Entra ID, Azure AD B2C or any OIDC issuer).</summary>
public interface IEntraClient
{
    /// <summary>Builds the <c>/authorize</c> redirect (authorization code + PKCE S256). A null prompt value and a
    /// null login hint emit no parameter at all, never an empty one.</summary>
    Uri BuildAuthorizeUrl(Uri redirectUri, string state, string codeChallenge, SignInPrompt prompt, string? loginHint = null);

    Task<EntraTokenResult> RedeemCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken);

    Task<EntraTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

public enum ExchangeOutcome
{
    Success,

    /// <summary>Keycloak's definitive "no": user unknown or disabled, exchange denied. Ends the session.</summary>
    Denied,

    /// <summary>5xx, timeout, network, or a client misconfiguration on our side. Never ends a session.</summary>
    Transient,
}

public sealed record ExchangeResult(ExchangeOutcome Outcome, string? Jwt = null)
{
    public static ExchangeResult Denied { get; } = new(ExchangeOutcome.Denied);

    public static ExchangeResult Transient { get; } = new(ExchangeOutcome.Transient);
}

public interface IKeycloakExchanger
{
    /// <summary>RFC 8693 exchange of the identity provider's access token for a JWT minted for this
    /// app's Keycloak client. Retries transient failures within the configured bound.</summary>
    Task<ExchangeResult> ExchangeAsync(string entraAccessToken, PortalOptions portal, CancellationToken cancellationToken);
}
