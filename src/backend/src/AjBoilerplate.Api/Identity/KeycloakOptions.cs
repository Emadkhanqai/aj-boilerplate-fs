namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Binding for the <c>Keycloak</c> config section.
///
/// Keycloak issues the only tokens this API validates. Behind the gateway the browser never holds one:
/// the Auth service exchanges the user's Entra ID token for a Keycloak JWT minted for THIS portal's
/// client, and nginx forwards it as a bearer token. See <c>docs/auth/gateway-contract.md</c>.
///
/// <see cref="Authority"/> and <see cref="ValidIssuer"/> are separate on purpose: a Keycloak fronted
/// by a gateway serves discovery/JWKS at the gateway's address, while the <c>iss</c> claim it stamps
/// into tokens is its own internal URL. Validating the issuer against the gateway address would
/// reject every real token.
/// </summary>
public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Base URL used for OIDC discovery when <see cref="JwksUri"/> is not set.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>This API's client id. Used to select the right entry in <c>resource_access</c> and to
    /// enforce the <c>azp</c> check — a shared realm issues tokens to other applications too.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Explicit JWKS endpoint. Preferred over discovery when set — see
    /// <see cref="KeycloakSigningKeyProvider"/> for why. Outside Development it must be https.</summary>
    public string JwksUri { get; init; } = string.Empty;

    /// <summary>The configuration key behind <see cref="AllowDiscoveryFallback"/>, spelled once here so
    /// the startup guard, its message and the tests all name the same string. As an environment
    /// variable it is <c>Keycloak__AllowDiscoveryFallback</c>.</summary>
    public const string AllowDiscoveryFallbackKey = "Keycloak:AllowDiscoveryFallback";

    /// <summary>
    /// The deliberate opt-in that lets an environment outside Development resolve signing keys by OIDC
    /// DISCOVERY instead of from an explicit <see cref="JwksUri"/>. Defaults to <c>false</c>, so a
    /// deployment that configures neither fails at startup rather than at the first authenticated
    /// request.
    ///
    /// <para>
    /// Why default-closed: behind a gateway, discovery can succeed in milliseconds while the
    /// <c>jwks_uri</c> it advertises never answers. The key fetch then stalls until the backchannel
    /// timeout (60 s by default, tried twice) and every authenticated request answers an unexplained
    /// 401 — measured on the CAD portal at 120150 ms per request, with <c>IDX10500: Signature
    /// validation failed. No security keys were provided</c> in the log. Setting this says the
    /// deployer has confirmed that <see cref="Authority"/>'s discovery document resolves AND
    /// advertises a reachable <c>jwks_uri</c> — the second half is the one that bites.
    /// </para>
    /// </summary>
    public bool AllowDiscoveryFallback { get; init; }

    /// <summary>
    /// Whether realm-wide roles (<c>realm_access.roles</c>) count as seats. Defaults to <c>false</c>:
    /// seats come only from this portal's own client roles (<c>resource_access.&lt;clientId&gt;</c>)
    /// and the flat <c>roles</c> claim the client's role mapper writes.
    ///
    /// <para>
    /// A shared realm carries roles that belong to other applications, so a realm-level <c>admin</c>
    /// granted for another portal would otherwise become this portal's administrative tier. Set it to
    /// <c>true</c> only against a local realm that grants this portal's seats as realm roles.
    /// </para>
    /// </summary>
    public bool HonourRealmRoles { get; init; }

    /// <summary>The exact <c>iss</c> value tokens carry.</summary>
    public string ValidIssuer { get; init; } = string.Empty;

    /// <summary>Leave true everywhere real. Only an isolated local/QA host with no TLS should ever
    /// turn it off, and doing so disables transport authentication of the metadata endpoint.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>True when <see cref="JwksUri"/> is set and is anything other than an absolute
    /// <c>https</c> URL.</summary>
    public bool JwksUriIsInsecure =>
        !string.IsNullOrWhiteSpace(JwksUri)
        && !(Uri.TryCreate(JwksUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// True when composition must be refused: a plaintext JWKS URI outside Development. A JWKS that
    /// arrives in plaintext can be substituted by anyone on the network path, and a substituted
    /// signing key means forged tokens accepted as genuine, for any role.
    /// </summary>
    public bool RefusesInsecureJwksUri(bool isDevelopment) => JwksUriIsInsecure && !isDevelopment;

    /// <summary>
    /// True when this configuration would silently degrade into the stall described on
    /// <see cref="AllowDiscoveryFallback"/>: no explicit <see cref="JwksUri"/>, outside Development,
    /// and discovery not opted into. <see cref="AuthenticationSetup"/> throws on it at composition.
    /// </summary>
    public bool RequiresExplicitJwksUri(bool isDevelopment) =>
        string.IsNullOrWhiteSpace(JwksUri) && !isDevelopment && !AllowDiscoveryFallback;

    /// <summary>True when enough is configured to validate a Keycloak-issued token.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ValidIssuer)
        && (!string.IsNullOrWhiteSpace(JwksUri) || !string.IsNullOrWhiteSpace(Authority));
}
