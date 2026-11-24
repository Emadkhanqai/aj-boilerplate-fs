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

    /// <summary>
    /// This app's Keycloak client: portal #0. Its host is <see cref="PublicOrigin"/>'s and its logged-out page is
    /// <see cref="LoggedOutPath"/>.
    /// </summary>
    [ValidateObjectMembers]
    public PortalOptions Portal { get; set; } = new();

    /// <summary>
    /// OPTIONAL further portals served by this one Auth service (<c>Auth:Portals:N</c>), each on its own host. Empty (the
    /// default) is a single-portal installation and the hand-off has nobody to hand off to. When non-empty the portal of a
    /// request is chosen by its forwarded host; see <see cref="ForwardedHostHeader"/> and <see cref="Portal"/>.
    /// </summary>
    public IList<PortalOptions> Portals { get; init; } = [];

    /// <summary>
    /// The header carrying the browser-facing host, set by the gateway. Read ONLY when <see cref="Portals"/> is non-empty,
    /// to choose the portal; the redirect URI is always built from the registered portal's configured origin, never from this.
    /// </summary>
    public string ForwardedHostHeader { get; set; } = "X-Forwarded-Host";

    /// <summary>Delivery tuning for the session-ended outbox.</summary>
    [ValidateObjectMembers]
    public SessionEndDeliveryOptions SessionEndDelivery { get; set; } = new();

    /// <summary>The Google-signed push of Keycloak user changes (<c>POST /auth/events</c>).</summary>
    [ValidateObjectMembers]
    public EventsOptions Events { get; set; } = new();

    [ValidateObjectMembers]
    public AdminOptions Admin { get; set; } = new();

    [ValidateObjectMembers]
    public AuthCookieOptions Cookies { get; set; } = new();

    /// <summary>
    /// Permit plain http to every upstream the start-up validator checks: <c>Entra:Authority</c>,
    /// <c>Entra:AuthorizeEndpointUrl</c>, <c>Entra:TokenEndpointUrl</c>, <c>Keycloak:BaseUrl</c> and
    /// <c>Keycloak:TokenEndpointUrl</c>. Several of them receive a client secret or a user's token.
    /// Default false: an http URL for any of them is then refused at start-up outside Development
    /// (<see cref="AuthOptionsValidator"/>).
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

    /// <summary>How long a portal hand-off ticket may wait to be redeemed (single use, in Redis).</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan HandoffTicketLifetime { get; set; } = TimeSpan.FromSeconds(60);

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

/// <summary>
/// The OIDC identity provider. <see cref="Authority"/> is the base under which <c>/oauth2/v2.0/authorize</c> and
/// <c>/oauth2/v2.0/token</c> live.
///
/// <para>
/// The DEFAULTS are the ones USP's staging Auth service runs against Microsoft Entra ID (a tenant id in the
/// authority, a Microsoft Graph scope, no own-client-id scope). They are not what Azure AD B2C needs, and the
/// difference is the part to keep:
/// </para>
/// <list type="bullet">
///   <item>Keycloak's <c>oidc</c> identity provider validates the subject token by calling Graph's userinfo
///     endpoint, which accepts only a token whose audience is Graph. So the Entra ACCESS token must be a Graph
///     token, and <c>User.Read</c> is what makes it one. <c>&lt;client-id&gt;/.default</c> was tried and the
///     token came back with this app as its audience, so the Keycloak exchange failed.</item>
///   <item>Appending the bare client id as a scope (<see cref="RequestOwnClientIdScope"/>) made Entra answer
///     AADSTS650053, "scope does not exist on the resource" (Graph). That is a B2C idiom: B2C issues an access
///     token only when the app's own client id is requested as a scope.</item>
/// </list>
/// <para>
/// To use B2C, set <c>Authority</c> to the policy URL, <c>Scopes</c> to <c>openid profile offline_access</c>,
/// <c>RequestOwnClientIdScope</c> to true and <c>Keycloak:SubjectIssuer</c> to the B2C provider's alias.
/// </para>
/// </summary>
public sealed class EntraOptions
{
    /// <summary>Entra: <c>https://login.microsoftonline.com/&lt;tenant-id&gt;</c>; the v2 endpoints are appended here.</summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary><c>offline_access</c> is what yields the refresh token the session lives on; <c>User.Read</c> makes the access token a Graph token (see the type remarks).</summary>
    [Required]
    public string Scopes { get; set; } = "openid profile offline_access User.Read";

    /// <summary>Also request the app's own client id as a scope. False for Entra (it breaks it, see the type remarks); true for B2C.</summary>
    public bool RequestOwnClientIdScope { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Full authorize URL, for an issuer whose endpoints are not <c>{Authority}/oauth2/v2.0/authorize</c>
    /// (the local Keycloak stand-in for Entra). Empty means derive it from <see cref="Authority"/>. This is
    /// where the BROWSER is sent, so it must be reachable from the browser.
    /// </summary>
    public string AuthorizeEndpointUrl { get; set; } = string.Empty;

    /// <summary>Full token URL, same reason as <see cref="AuthorizeEndpointUrl"/>. This is POSTed by the service, so it must be reachable from the service, which is not always the browser's address.</summary>
    public string TokenEndpointUrl { get; set; } = string.Empty;

    public string EffectiveScopes =>
        RequestOwnClientIdScope && !Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ClientId, StringComparer.Ordinal)
            ? $"{Scopes} {ClientId}"
            : Scopes;

    public Uri AuthorizeEndpoint => new(string.IsNullOrWhiteSpace(AuthorizeEndpointUrl)
        ? $"{Authority.TrimEnd('/')}/oauth2/v2.0/authorize"
        : AuthorizeEndpointUrl);

    public Uri TokenEndpoint => new(string.IsNullOrWhiteSpace(TokenEndpointUrl)
        ? $"{Authority.TrimEnd('/')}/oauth2/v2.0/token"
        : TokenEndpointUrl);
}

public sealed class KeycloakOptions
{
    /// <summary>The realm's issuer base, e.g. <c>https://keycloak.example.com</c> (no <c>/realms/...</c>).</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string Realm { get; set; } = "default";

    /// <summary>
    /// Alias of the Keycloak identity provider that trusts the sign-in token (<c>subject_issuer</c>). <c>oidc</c> is
    /// what USP's Keycloak calls its Entra ID provider; B2C has its own alias. EMPTY omits the parameter, which
    /// makes the call an internal (Keycloak to Keycloak) exchange: only for a local realm that plays the identity
    /// provider itself, and refused outside Development.
    /// </summary>
    public string SubjectIssuer { get; set; } = "oidc";

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

/// <summary>
/// A portal this Auth service signs users in to: its Keycloak client, and (for portals beyond #0) its host. Portal #0
/// is <see cref="AuthOptions.Portal"/> and takes its host from <see cref="AuthOptions.PublicOrigin"/>.
/// </summary>
public sealed class PortalOptions
{
    /// <summary>The Keycloak client whose roles the portal's API reads. Also the key the hand-off names a portal by.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Browser-facing host, bare (no scheme, no port), lower-case. Required for <c>Auth:Portals:N</c>; ignored for portal #0.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Where a plain logout lands on this portal. Empty uses <see cref="AuthOptions.LoggedOutPath"/>. Ignored for portal #0.</summary>
    public string LoggedOutPath { get; set; } = string.Empty;

    /// <summary>The name pages use for this portal ("You have no access to {DisplayName}"). Empty reads "this application".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Where this portal is told its sessions ended. Not configured: nothing is queued for this portal.</summary>
    public SessionEventsOptions SessionEvents { get; set; } = new();

    /// <summary>Set by <see cref="PortalRegistry"/> for portal #0 (the whole configured <c>PublicOrigin</c>, port and scheme included).</summary>
    internal string? OriginOverride { get; set; }

    /// <summary>Browser-facing origin: the redirect URI is this plus <c>/auth/callback</c>, and a logout POST must come from it.</summary>
    public string Origin => OriginOverride ?? $"https://{Host}";
}

/// <summary>
/// Where one portal receives <c>session.ended</c> events (docs/auth/session-ended-contract.md). Not configured (no <see cref="Url"/>):
/// nothing is queued for that portal, so a portal that has not deployed its receiver is not sent events it would answer 404 to.
/// </summary>
public sealed class SessionEventsOptions
{
    /// <summary>The portal backend's receiver, e.g. <c>https://api.example.com/api/integration/auth-backend/session-ended</c>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The Google ID token audience, when the receiver authenticates with one. Empty: the origin of <see cref="Url"/>.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret sent as <c>X-Auth-Events-Key</c>. When set it is what authenticates the call (a Google ID token is then not
    /// minted); the receiver compares it in constant time. Comes from the secrets provider, never from a file.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    public bool UsesKey => !string.IsNullOrWhiteSpace(Key);

    public string? ResolvedAudience =>
        !string.IsNullOrWhiteSpace(Audience)
            ? Audience.Trim()
            : Uri.TryCreate(Url, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : null;
}

/// <summary>Delivery tuning for the session-ended outbox. The defaults are USP's.</summary>
public sealed class SessionEndDeliveryOptions
{
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claimed event is reserved for the claimant; a crashed instance's claim simply runs out.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    [Range(typeof(TimeSpan), "00:00:01", "06:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An event still undelivered this long after it was queued is abandoned and logged at Error.</summary>
    [Range(typeof(TimeSpan), "00:10:00", "30.00:00:00")]
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(48);

    /// <summary>How long a delivered or abandoned event stays before it is purged.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// The Google-signed push of Keycloak user changes to <c>POST /auth/events</c> (Pub/Sub, OIDC token). Disabled (404) unless both
/// values are set.
/// </summary>
public sealed class EventsOptions
{
    /// <summary>The ID token audience the push is configured with; compared exactly.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The Pub/Sub push service account's e-mail.</summary>
    public string PushServiceAccount { get; set; } = string.Empty;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(PushServiceAccount);
}

public sealed class AdminOptions
{
    /// <summary>Shared secret for <c>/internal/admin/*</c>, sent as <c>X-Admin-Api-Key</c>. Empty disables the key (see <see cref="IsEnabled"/>).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// OPTIONAL alternative caller authentication: a Google ID token whose <c>aud</c> is this value and whose e-mail is one of
    /// <see cref="AllowedCallers"/> (signature always verified). Accepted IN ADDITION to the key; both empty is not a way in.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    public IList<string> AllowedCallers { get; init; } = [];

    /// <summary>
    /// Keycloak group paths this surface may administer, e.g. <c>/my-app</c>. A seat is a group UNDER one of them
    /// (<c>/my-app/editors</c>); a path outside every managed path is refused and nothing is changed.
    /// </summary>
    public IList<string> ManagedGroupPaths { get; init; } = [];

    /// <summary>
    /// E-mail domains a NEW account may be created for. DENY BY DEFAULT: empty refuses every creation.
    /// </summary>
    public IList<string> AllowedEmailDomains { get; init; } = [];

    /// <summary>The Keycloak Admin REST API client behind the directory routes.</summary>
    [ValidateObjectMembers]
    public KeycloakAdminOptions Keycloak { get; set; } = new();

    public bool GoogleCallerConfigured => !string.IsNullOrWhiteSpace(Audience) && AllowedCallers.Count > 0;

    /// <summary>The whole <c>/internal/admin</c> group exists only when a caller can authenticate to it.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ApiKey) || GoogleCallerConfigured;
}

/// <summary>
/// A dedicated Keycloak client with a service account (client-credentials), holding the realm-management roles
/// <c>view-users</c>, <c>manage-users</c> and <c>query-groups</c> / <c>query-users</c>. NOT this app's own client.
/// </summary>
public sealed class KeycloakAdminOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Overrides <c>Auth:Keycloak:BaseUrl</c> for the admin API. Empty uses it.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Overrides <c>Auth:Keycloak:Realm</c> for the admin API. Empty uses it.</summary>
    public string Realm { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class AuthCookieOptions
{
    /// <summary>True (default): <c>__Host-</c> cookie names and the Secure flag. False is for local http only and is refused outside Development.</summary>
    public bool Secure { get; set; } = true;

    public string SessionName => Secure ? "__Host-app_sid" : "app_sid";

    public string LoginName => Secure ? "__Host-app_login" : "app_login";

    /// <summary>One-shot marker set by a hand-off redeem that found no role, spent by the first <c>/auth/no-access</c> view. Carries no identity.</summary>
    public string NoAccessName => Secure ? "__Host-app_noaccess" : "app_noaccess";
}
