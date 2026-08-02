namespace AjBoilerplate.Api.Identity;

/// <summary>One upstream identity provider's OIDC settings.</summary>
public sealed class IdentityProviderOptions
{
    /// <summary>OIDC authority — the issuer base URL discovery is performed against.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The audience (this API's identifier) tokens must be issued for. Validating it is
    /// what stops a token minted for a DIFFERENT application in the same tenant from being accepted
    /// here.</summary>
    public string Audience { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Bound from the <c>Authentication</c> config section — the AUTHENTICATION (who are you) side,
/// which is the one identity concern that differs per cloud.
///
/// Authorization (what may you do) is Keycloak on both clouds and never branches: see
/// <see cref="KeycloakOptions"/>. When Keycloak is configured it also issues the tokens this API
/// accepts, federating to whichever provider below is selected, and these settings go unused —
/// that is the recommended deployment. They exist for the simpler topology where the API validates
/// the cloud IdP's tokens directly.
/// </summary>
public sealed class UpstreamIdentityOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Used when <c>CLOUD_PROVIDER=gcp</c>.</summary>
    public IdentityProviderOptions GoogleCloudIdentity { get; set; } = new();

    /// <summary>Used when <c>CLOUD_PROVIDER=azure</c>.</summary>
    public IdentityProviderOptions EntraId { get; set; } = new();
}
