using Google.Cloud.SecretManager.V1;

namespace AjBoilerplate.Api.Configuration;

/// <summary>
/// Adds Google Cloud Secret Manager as a configuration source, using
/// <see cref="SecretManagerServiceClient.Create"/> — Application Default Credentials, i.e. the
/// workload/service identity in the cloud and <c>gcloud auth application-default login</c> locally.
/// Selected by <c>CLOUD_PROVIDER=gcp</c> (the default); a blank project id makes this a no-op so
/// configuration falls back to appsettings, user-secrets, and environment variables, exactly
/// mirroring <see cref="KeyVaultConfiguration.AddAzureKeyVaultIfConfigured"/>.
///
/// There is no Google-published ASP.NET Core <see cref="IConfigurationSource"/> package for Secret
/// Manager (unlike Azure Key Vault's <c>Azure.Extensions.AspNetCore.Configuration.Secrets</c>), so
/// this source/provider pair is written directly against <see cref="SecretManagerServiceClient"/>.
///
/// Secret-id-to-configuration-key mapping: a configuration key containing <c>:</c> is not a legal
/// Secret Manager secret id, so this provider uses the same <c>--</c> convention Key Vault uses for
/// the same reason — a secret named <c>ConnectionStrings--Default</c> resolves to the configuration
/// key <c>ConnectionStrings:Default</c>.
/// </summary>
public static class GcpSecretManagerConfiguration
{
    public static WebApplicationBuilder AddGcpSecretManagerIfConfigured(this WebApplicationBuilder builder, string? projectId)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            // ConfigurationManager explicitly implements IConfigurationBuilder.Add (to avoid an
            // ambiguity with the other builder-ish interfaces it also implements), so it needs this
            // explicit cast.
            ((IConfigurationBuilder)builder.Configuration).Add(new GcpSecretManagerConfigurationSource(projectId));
        }

        return builder;
    }
}

/// <summary><see cref="IConfigurationSource"/> for <see cref="GcpSecretManagerConfiguration"/> — see
/// its remarks for the full design.</summary>
public sealed class GcpSecretManagerConfigurationSource : IConfigurationSource
{
    private readonly string _projectId;

    public GcpSecretManagerConfigurationSource(string projectId) => _projectId = projectId;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new GcpSecretManagerConfigurationProvider(_projectId);
}

/// <summary><see cref="ConfigurationProvider"/> for <see cref="GcpSecretManagerConfiguration"/> —
/// see its remarks for the secret-id-to-configuration-key mapping convention.</summary>
public sealed class GcpSecretManagerConfigurationProvider : ConfigurationProvider
{
    private const string SecretIdKeyDelimiter = "--";

    private readonly string _projectId;
    private readonly SecretManagerServiceClient? _client;

    public GcpSecretManagerConfigurationProvider(string projectId) : this(projectId, client: null)
    {
    }

    /// <summary>Test seam: accepts a pre-built client so contract tests can substitute a fake without
    /// touching real credentials or the network.</summary>
    internal GcpSecretManagerConfigurationProvider(string projectId, SecretManagerServiceClient? client)
    {
        _projectId = projectId;
        _client = client;
    }

    public override void Load()
    {
        var client = _client ?? SecretManagerServiceClient.Create();
        var projectName = new Google.Api.Gax.ResourceNames.ProjectName(_projectId);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in client.ListSecrets(projectName))
        {
            var secretId = SecretName.Parse(secret.Name).SecretId;
            var configKey = secretId.Replace(SecretIdKeyDelimiter, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            var versionName = new SecretVersionName(_projectId, secretId, "latest");
            var accessed = client.AccessSecretVersion(versionName);
            data[configKey] = accessed.Payload.Data.ToStringUtf8();
        }

        Data = data;
    }
}
