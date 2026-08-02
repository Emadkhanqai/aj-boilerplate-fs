using AjBoilerplate.Infrastructure.Cloud;

namespace AjBoilerplate.Api.Configuration;

/// <summary>
/// The BOOT-TIME half of the secrets story: loads the selected cloud's secret store into
/// <see cref="IConfiguration"/> before the host starts, so a connection string or a signing key
/// simply appears as ordinary configuration and no consumer needs to know a cloud is involved. The
/// runtime half — fetching a secret fresh, without a restart — is
/// <c>AjBoilerplate.Application.Abstractions.ISecretsProvider</c>.
///
/// Which store is loaded is decided by <c>CLOUD_PROVIDER</c> (<c>Cloud:Provider</c>). Exactly one is
/// ever added, so there is no last-registered-wins ambiguity between two clouds' copies of the same
/// key. When the selected provider's store is not configured, this is a no-op and configuration
/// falls back to appsettings, user-secrets, and environment variables — the local/test/offline path.
/// </summary>
public static class CloudSecretsConfiguration
{
    /// <summary>
    /// The environment variable a deployment sets. Mapped onto the <c>Cloud:Provider</c>
    /// configuration key so the same value is readable both as an env var (what a container
    /// orchestrator supplies) and as ordinary configuration (what <see cref="CloudOptions"/> binds).
    /// </summary>
    public const string ProviderEnvironmentVariable = "CLOUD_PROVIDER";

    public static WebApplicationBuilder AddCloudSecrets(this WebApplicationBuilder builder)
    {
        // Read CLOUD_PROVIDER first, then fall back to a Cloud:Provider already present in
        // appsettings, then to the default. Adding it back as an in-memory configuration entry means
        // CloudOptions binds identically no matter which of the two ways it was supplied.
        var provider = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(provider))
        {
            builder.Configuration.AddInMemoryCollection(
                [new KeyValuePair<string, string?>($"{CloudOptions.SectionName}:Provider", provider)]);
        }

        var cloud = builder.Configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();

        // Resolve() throws on an unrecognised value — a typo must fail loudly at startup rather than
        // silently reading secrets from the wrong cloud, or from none.
        switch (cloud.Resolve())
        {
            case CloudProvider.Azure:
                builder.AddAzureKeyVaultIfConfigured(cloud.Azure.KeyVaultUri);
                break;

            case CloudProvider.Gcp:
            default:
                builder.AddGcpSecretManagerIfConfigured(cloud.Gcp.ProjectId);
                break;
        }

        return builder;
    }
}
