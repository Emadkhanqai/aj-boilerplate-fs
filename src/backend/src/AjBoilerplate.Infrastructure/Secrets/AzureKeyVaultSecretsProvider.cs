using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Infrastructure.Cloud;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Secrets;

/// <summary>
/// <see cref="ISecretsProvider"/> over Azure Key Vault. Registered when <c>CLOUD_PROVIDER=azure</c>.
///
/// Credentials come from <see cref="DefaultAzureCredential"/> — the Managed Identity attached to the
/// runtime in the cloud, the developer's Azure CLI/VS sign-in locally. No client secret is ever read
/// from a file, and none is ever committed.
///
/// The client is built lazily on first use for the same reason as the GCP provider: constructing it
/// resolves credentials, and the application must be able to start on a machine with no Azure
/// credentials at all as long as nothing actually asks for a secret.
/// </summary>
public sealed class AzureKeyVaultSecretsProvider : ISecretsProvider
{
    private readonly Lazy<SecretClient> _client;

    public AzureKeyVaultSecretsProvider(IOptions<CloudOptions> options)
        : this(options, uri => new SecretClient(uri, new DefaultAzureCredential()))
    {
    }

    /// <summary>Test seam: accepts a client factory so a contract test can substitute a fake without
    /// touching real credentials or the network.</summary>
    internal AzureKeyVaultSecretsProvider(IOptions<CloudOptions> options, Func<Uri, SecretClient> clientFactory)
    {
        var vaultUri = options.Value.Azure.KeyVaultUri;
        if (string.IsNullOrWhiteSpace(vaultUri) || !Uri.TryCreate(vaultUri, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "Cloud:Azure:KeyVaultUri is not a valid absolute URI, but CLOUD_PROVIDER is 'azure'. Set " +
                "the vault URI that holds this application's secrets.");
        }

        _client = new Lazy<SecretClient>(() => clientFactory(uri), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var response = await _client.Value.GetSecretAsync(name, cancellationToken: cancellationToken);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.NotFound)
        {
            // A secret that was never created is a foreseeable "not configured" state, not a failure
            // — see ISecretsProvider. Every other status (403, 503) still propagates.
            return null;
        }
    }

    private static class StatusCodes
    {
        public const int NotFound = 404;
    }
}
