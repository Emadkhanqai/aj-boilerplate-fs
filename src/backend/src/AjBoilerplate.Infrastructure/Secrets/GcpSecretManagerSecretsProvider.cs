using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Infrastructure.Cloud;
using Google;
using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Secrets;

/// <summary>
/// <see cref="ISecretsProvider"/> over Google Cloud Secret Manager. Registered when
/// <c>CLOUD_PROVIDER=gcp</c>.
///
/// Credentials come from Application Default Credentials — the workload/service identity attached to
/// the runtime in the cloud, a developer's <c>gcloud auth application-default login</c> locally. No
/// key file is ever read from disk, and none is ever committed.
///
/// The client is built lazily on first use rather than in the constructor: constructing it resolves
/// credentials and opens a gRPC channel, and a boilerplate must be able to start (and to run its
/// tests) on a machine with no GCP credentials at all, as long as nothing actually asks for a secret.
/// </summary>
public sealed class GcpSecretManagerSecretsProvider : ISecretsProvider
{
    private readonly string _projectId;
    private readonly Lazy<SecretManagerServiceClient> _client;

    public GcpSecretManagerSecretsProvider(IOptions<CloudOptions> options)
        : this(options, () => SecretManagerServiceClient.Create())
    {
    }

    /// <summary>Test seam: accepts a client factory so a contract test can substitute a fake without
    /// touching real credentials or the network.</summary>
    internal GcpSecretManagerSecretsProvider(IOptions<CloudOptions> options, Func<SecretManagerServiceClient> clientFactory)
    {
        _projectId = options.Value.Gcp.ProjectId;
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            throw new InvalidOperationException(
                "Cloud:Gcp:ProjectId is not configured, but CLOUD_PROVIDER is 'gcp'. Set the project id " +
                "that owns this application's Secret Manager secrets.");
        }

        _client = new Lazy<SecretManagerServiceClient>(clientFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var versionName = new SecretVersionName(_projectId, name, "latest");
        try
        {
            var response = await _client.Value.AccessSecretVersionAsync(versionName, cancellationToken);
            return response.Payload.Data.ToStringUtf8();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // A secret that was never created is a foreseeable "not configured" state, not a failure
            // — see ISecretsProvider. Every other status (permission denied, unavailable) still
            // propagates, because those are real problems an operator must see.
            return null;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
