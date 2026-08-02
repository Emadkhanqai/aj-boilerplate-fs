namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Binding for the <c>Keycloak</c> config section.
///
/// Keycloak is the AUTHORIZATION layer and is provider-independent: it issues the same tokens, with
/// the same role claims, whichever cloud <c>CLOUD_PROVIDER</c> selects. Only the upstream identity
/// provider it federates to differs (Google Cloud Identity vs Microsoft Entra ID), and that is
/// configured on the Keycloak side, not here.
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

    public string Realm { get; init; } = string.Empty;

    /// <summary>This API's client id. Used to select the right entry in <c>resource_access</c> and to
    /// enforce the <c>azp</c> check — a shared realm issues tokens to other applications too.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Never set in a committed file — supply it from the cloud secret store or the
    /// environment.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Explicit JWKS endpoint. Preferred over discovery when set — see
    /// <see cref="KeycloakSigningKeyProvider"/> for why.</summary>
    public string JwksUri { get; init; } = string.Empty;

    /// <summary>The exact <c>iss</c> value tokens carry.</summary>
    public string ValidIssuer { get; init; } = string.Empty;

    /// <summary>Leave true everywhere real. Only an isolated local/QA host with no TLS should ever
    /// turn it off, and doing so disables transport authentication of the metadata endpoint.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>True when enough is configured to validate a Keycloak-issued token.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ValidIssuer)
        && (!string.IsNullOrWhiteSpace(JwksUri) || !string.IsNullOrWhiteSpace(Authority));
}
