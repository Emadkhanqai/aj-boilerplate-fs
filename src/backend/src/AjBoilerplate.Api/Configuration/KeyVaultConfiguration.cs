using Azure.Identity;

namespace AjBoilerplate.Api.Configuration;

/// <summary>
/// Adds Azure Key Vault as a configuration source, using <see cref="DefaultAzureCredential"/> —
/// Managed Identity in the cloud, the developer's Azure CLI/VS sign-in locally. No client secret is
/// ever read from a committed file. Selected by <c>CLOUD_PROVIDER=azure</c>; a blank vault URI makes
/// this a no-op so configuration falls back to appsettings, user-secrets, and environment variables.
///
/// Key Vault secret names cannot contain <c>:</c>, so the SDK's own convention maps <c>--</c> to the
/// configuration key delimiter: a secret named <c>ConnectionStrings--Default</c> arrives as
/// <c>ConnectionStrings:Default</c>. <see cref="GcpSecretManagerConfiguration"/> deliberately uses
/// the same convention.
/// </summary>
public static class KeyVaultConfiguration
{
    public static WebApplicationBuilder AddAzureKeyVaultIfConfigured(this WebApplicationBuilder builder, string? vaultUri)
    {
        if (!string.IsNullOrWhiteSpace(vaultUri) && Uri.TryCreate(vaultUri, UriKind.Absolute, out var uri))
        {
            builder.Configuration.AddAzureKeyVault(uri, new DefaultAzureCredential());
        }

        return builder;
    }
}
