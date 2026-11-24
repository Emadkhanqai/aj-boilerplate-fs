using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Configuration;

/// <summary>Root of the <c>Auth</c> configuration section. Secrets are never defaults: they come from the secrets provider.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// The browser-facing origin of the app, e.g. <c>https://app.example.com</c> (scheme and host, no path).
    /// The redirect URI registered with the identity provider is this plus <c>/auth/callback</c>, and a logout
    /// POST must come from this origin. It is configuration, never read from a request header.
    /// </summary>
    [Required]
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>Where a plain logout lands. nginx serves it without a session.</summary>
    [Required]
    public string LoggedOutPath { get; set; } = "/logged-out";

    [ValidateObjectMembers]
    public AuthSessionOptions Session { get; set; } = new();

    /// <summary>The identity provider users sign in to: Entra ID, Azure AD B2C or any OIDC issuer.</summary>
    [ValidateObjectMembers]
    public EntraOptions Entra { get; set; } = new();

    [ValidateObjectMembers]
    public KeycloakOptions Keycloak { get; set; } = new();

    /// <summary>This app's Keycloak client.</summary>
    [ValidateObjectMembers]
    public PortalOptions Portal { get; set; } = new();

    [ValidateObjectMembers]
    public AdminOptions Admin { get; set; } = new();

    [ValidateObjectMembers]
    public AuthCookieOptions Cookies { get; set; } = new();

    /// <summary>
    /// Permit plain http to <c>Keycloak:TokenEndpointUrl</c>, which receives a client secret and a user's
    /// token. Default false: http is then refused at start-up outside Development.
    /// </summary>
    public bool AllowInsecureUpstreams { get; set; }
}

public sealed class AuthSessionOptions
{
    /// <summary>Sliding idle timeout; every successful validation pushes it out again.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Absolute lifetime, measured from sign-in. Activity does not extend it.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "30.00:00:00")]
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Refresh this long BEFORE the JWT expires: the resilience margin a transient fault is survived inside.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "00:30:00")]
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a session ended by a definitive no-role keeps answering <c>403 revoked</c> instead of 401.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan RevokedMarkerLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a pending sign-in (PKCE verifier + state) may take.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan LoginStateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Single-flight refresh lock lifetime. It is also what a request that LOST the lock waits for
    /// (<see cref="RefreshWaitBudget"/>): one number, two uses, so the waiter can never give up while the
    /// winner still legitimately holds the lock.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan RefreshLockLifetime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Interval a request that lost the refresh lock waits between session re-reads.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:01")]
    public TimeSpan LockPollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How far PAST the lock's expiry a loser keeps waiting, so it can observe a refresh finished on the last millisecond.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:05")]
    public TimeSpan LockExpiryGrace { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on <see cref="RefreshWaitBudget"/>: the worst case a user waits on a blank page. Enforced at start-up.</summary>
    public static readonly TimeSpan MaxRefreshWait = TimeSpan.FromSeconds(10);

    /// <summary>DERIVED, not settable: always longer than <see cref="RefreshLockLifetime"/>.</summary>
    public TimeSpan RefreshWaitBudget => RefreshLockLifetime + LockExpiryGrace;

    /// <summary>The wait budget as a poll COUNT. Bounded by count rather than a wall clock so a frozen test clock still terminates it.</summary>
    public int RefreshWaitAttempts =>
        LockPollInterval <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(RefreshWaitBudget / LockPollInterval);

    /// <summary>A token closer than this to expiry is not served as a "still valid" fallback.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan MinimumServableLifetime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Base64 of 32 random bytes. Encrypts the JWT and refresh token at rest in SQL and Redis (AES-256-GCM).</summary>
    [Required]
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>How often expired rows are swept.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>The OIDC identity provider. <see cref="Authority"/> is the base under which <c>/oauth2/v2.0/authorize</c> and <c>/oauth2/v2.0/token</c> live.</summary>
public sealed class EntraOptions
{
    [Required]
    public string Authority { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary><c>offline_access</c> is what yields the refresh token the session lives on.</summary>
    [Required]
    public string Scopes { get; set; } = "openid profile offline_access";

    /// <summary>Also request the app's own client id as a scope: B2C only issues an access token (what Keycloak exchanges) when a resource scope is requested.</summary>
    public bool RequestOwnClientIdScope { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public string EffectiveScopes =>
        RequestOwnClientIdScope && !Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ClientId, StringComparer.Ordinal)
            ? $"{Scopes} {ClientId}"
            : Scopes;

    public Uri AuthorizeEndpoint => new($"{Authority.TrimEnd('/')}/oauth2/v2.0/authorize");

    public Uri TokenEndpoint => new($"{Authority.TrimEnd('/')}/oauth2/v2.0/token");
}

public sealed class KeycloakOptions
{
    /// <summary>The realm's issuer base, e.g. <c>https://keycloak.example.com</c> (no <c>/realms/...</c>).</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string Realm { get; set; } = "default";

    /// <summary>Alias of the identity provider inside the realm (<c>subject_issuer</c>).</summary>
    [Required]
    public string SubjectIssuer { get; set; } = string.Empty;

    [Required]
    public string Scope { get; set; } = "openid profile email";

    /// <summary>Send <c>audience=&lt;client id&gt;</c> on the exchange.</summary>
    public bool SendAudience { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 5)]
    public int MaxAttempts { get; set; } = 3;

    [Range(typeof(TimeSpan), "00:00:00", "00:00:05")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Where the exchange is actually POSTed, when the issuer identifier is not an address you can call. Empty means derive it from <see cref="BaseUrl"/> and <see cref="Realm"/>.</summary>
    public string TokenEndpointUrl { get; set; } = string.Empty;

    public Uri TokenEndpoint => string.IsNullOrWhiteSpace(TokenEndpointUrl)
        ? new Uri($"{BaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(Realm)}/protocol/openid-connect/token")
        : new Uri(TokenEndpointUrl);
}

/// <summary>This app's own Keycloak client (the one whose roles the API reads).</summary>
public sealed class PortalOptions
{
    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class AdminOptions
{
    /// <summary>Shared secret for <c>/internal/admin/*</c>, sent as <c>X-Admin-Api-Key</c>. Empty disables the endpoints (404).</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class AuthCookieOptions
{
    /// <summary>True (default): <c>__Host-</c> cookie names and the Secure flag. False is for local http only and is refused outside Development.</summary>
    public bool Secure { get; set; } = true;

    public string SessionName => Secure ? "__Host-app_sid" : "app_sid";

    public string LoginName => Secure ? "__Host-app_login" : "app_login";
}
