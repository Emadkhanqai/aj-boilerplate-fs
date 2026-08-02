namespace AjBoilerplate.Infrastructure.Cloud;

/// <summary>The cloud this deployment runs on.</summary>
public enum CloudProvider
{
    /// <summary>Google Cloud: Secret Manager for secrets, Google Cloud Identity for authentication.</summary>
    Gcp,

    /// <summary>Microsoft Azure: Key Vault (+ Managed Identity) for secrets, Microsoft Entra ID for
    /// authentication.</summary>
    Azure,
}

/// <summary>
/// Bound from the <c>Cloud</c> config section, whose <c>Provider</c> key is conventionally supplied
/// as the <c>CLOUD_PROVIDER</c> environment variable (see <c>Program.cs</c>, which maps it).
///
/// Exactly two concerns branch on this, and both are behind an interface:
/// <list type="bullet">
///   <item><b>Secrets</b> — <c>ISecretsProvider</c> plus the boot-time configuration source.</item>
///   <item><b>Authentication</b> — which issuer's tokens are accepted. Authorization is Keycloak on
///     both clouds and never branches.</item>
/// </list>
/// Notably, the CACHE does not branch. Memorystore for Redis and Azure Cache for Redis both speak
/// the Redis wire protocol, so the same <c>ConnectionStrings:Redis</c> and the same
/// <c>AddStackExchangeRedisCache</c> registration serve both — the difference lives entirely in
/// <c>infra/</c>. The database does not branch either (SQL Server on both).
/// </summary>
public sealed class CloudOptions
{
    public const string SectionName = "Cloud";

    /// <summary>The selected provider as configured. Parsed by <see cref="Resolve"/>; kept as a
    /// string here so a bad value produces a clear startup error rather than a silent binding
    /// failure to the enum's default.</summary>
    public string Provider { get; set; } = DefaultProvider;

    /// <summary>Used when <see cref="Provider"/> is blank.</summary>
    public const string DefaultProvider = "gcp";

    /// <summary>Google Cloud settings. Ignored when <see cref="Provider"/> is <c>azure</c>.</summary>
    public GcpCloudOptions Gcp { get; set; } = new();

    /// <summary>Azure settings. Ignored when <see cref="Provider"/> is <c>gcp</c>.</summary>
    public AzureCloudOptions Azure { get; set; } = new();

    /// <summary>
    /// <see cref="Provider"/> as the enum. Throws on an unrecognised value: a typo must fail loudly
    /// at startup, never fall through to "whichever provider the enum happens to default to" and
    /// then read secrets from the wrong cloud (or, worse, from no cloud at all).
    /// </summary>
    public CloudProvider Resolve()
    {
        var raw = string.IsNullOrWhiteSpace(Provider) ? DefaultProvider : Provider.Trim();
        return raw.ToLowerInvariant() switch
        {
            "gcp" => CloudProvider.Gcp,
            "azure" => CloudProvider.Azure,
            _ => throw new InvalidOperationException(
                $"Cloud:Provider (CLOUD_PROVIDER) is '{Provider}'. Supported values are 'gcp' and 'azure'."),
        };
    }
}

/// <summary>Google Cloud settings, bound from <c>Cloud:Gcp</c>.</summary>
public sealed class GcpCloudOptions
{
    /// <summary>The project whose Secret Manager holds this application's secrets. Blank disables
    /// the secrets integration entirely (local dev, tests, offline).</summary>
    public string ProjectId { get; set; } = string.Empty;
}

/// <summary>Azure settings, bound from <c>Cloud:Azure</c>.</summary>
public sealed class AzureCloudOptions
{
    /// <summary>The Key Vault holding this application's secrets, e.g.
    /// <c>https://&lt;your-vault&gt;.vault.azure.net/</c>. Blank disables the secrets integration
    /// entirely (local dev, tests, offline).</summary>
    public string KeyVaultUri { get; set; } = string.Empty;
}
