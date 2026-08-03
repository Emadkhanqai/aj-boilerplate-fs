namespace AjBoilerplate.Infrastructure.Storage;

/// <summary>
/// Bound from the <c>Storage</c> config section. Which <see cref="Application.Abstractions.IFileStorage"/>
/// is registered follows the same <c>CLOUD_PROVIDER</c> switch <c>ISecretsProvider</c> uses — see
/// <c>DependencyInjection.AddStorage</c>.
/// </summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Used when <see cref="LocalRoot"/> is blank.</summary>
    public const string DefaultLocalRoot = "App_Data/storage";

    /// <summary>
    /// Directory the local implementation writes under. Relative paths resolve against the process's
    /// working directory. Only consulted when no cloud bucket or container is configured.
    /// </summary>
    public string LocalRoot { get; set; } = DefaultLocalRoot;

    /// <summary><see cref="LocalRoot"/> with a blank value falling back to
    /// <see cref="DefaultLocalRoot"/>, so an empty configured value cannot resolve the storage root to
    /// the process's working directory itself.</summary>
    public string ResolvedLocalRoot =>
        string.IsNullOrWhiteSpace(LocalRoot) ? DefaultLocalRoot : LocalRoot.Trim();

    /// <summary>Google Cloud Storage settings. Ignored when <c>CLOUD_PROVIDER</c> is <c>azure</c>.</summary>
    public GcpStorageOptions Gcp { get; set; } = new();

    /// <summary>Azure Blob Storage settings. Ignored when <c>CLOUD_PROVIDER</c> is <c>gcp</c>.</summary>
    public AzureStorageOptions Azure { get; set; } = new();
}

/// <summary>Google Cloud Storage settings, bound from <c>Storage:Gcp</c>.</summary>
public sealed class GcpStorageOptions
{
    /// <summary>The bucket this application's files live in. Blank selects the local implementation.</summary>
    public string Bucket { get; set; } = string.Empty;
}

/// <summary>Azure Blob Storage settings, bound from <c>Storage:Azure</c>.</summary>
public sealed class AzureStorageOptions
{
    /// <summary>The blob container this application's files live in. Blank selects the local
    /// implementation.</summary>
    public string Container { get; set; } = string.Empty;
}
