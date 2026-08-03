using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> over the local filesystem, used when no cloud bucket or container is
/// configured (local dev, tests, offline) — the same conditional-registration shape as
/// <see cref="Email.LoggingEmailSender"/> and <see cref="Secrets.NullSecretsProvider"/>.
///
/// Unlike those two it is a REAL implementation, not a no-op: files genuinely round-trip, so a flow
/// that saves and re-reads an attachment behaves the same locally as it will in the cloud. A no-op
/// here would be worse than useless — code would appear to work until the first read.
///
/// <para>
/// <b>Not for multi-instance deployments.</b> The filesystem is process-local, so two instances do
/// not see each other's files. That is the same trap <c>AddInfrastructure</c>'s Redis fallback has,
/// and <c>AddStorage</c> refuses to select this implementation outside Development for exactly that
/// reason rather than leaving it to be discovered after a scale-out.
/// </para>
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(IOptions<FileStorageOptions> options, ILogger<LocalFileStorage> logger)
    {
        _root = Path.GetFullPath(options.Value.ResolvedLocalRoot);
        _logger = logger;
    }

    public async Task<string> SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        // The media type has nowhere to live on a filesystem, so it is written beside the file. A
        // cloud object store records it as object metadata; not persisting it here would make
        // OpenReadAsync hand back a different content type locally than in the cloud, which is the
        // sort of difference that only shows up as a broken download in a browser.
        await File.WriteAllTextAsync(ContentTypePathFor(path), contentType, cancellationToken);

        return key;
    }

    public Task<StoredFile?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<StoredFile?>(null);
        }

        var contentTypePath = ContentTypePathFor(path);
        var contentType = File.Exists(contentTypePath)
            ? File.ReadAllText(contentTypePath)
            : "application/octet-stream";

        var stream = File.OpenRead(path);
        return Task.FromResult<StoredFile?>(new StoredFile(stream, contentType, stream.Length));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);

        var contentTypePath = ContentTypePathFor(path);
        if (File.Exists(contentTypePath))
        {
            File.Delete(contentTypePath);
        }

        return Task.FromResult(true);
    }

    private static string ContentTypePathFor(string path) => path + ".contenttype";

    /// <summary>
    /// Turns an opaque storage key into a path inside <see cref="_root"/>, refusing anything that
    /// would escape it.
    ///
    /// THIS IS THE SECURITY BOUNDARY OF THIS CLASS. A key is frequently derived from user input, so
    /// <c>../../appsettings.json</c> is the expected attack, not a hypothetical one. The check is done
    /// on the FULLY RESOLVED path rather than by scanning the key for <c>..</c>: substring checks miss
    /// encodings, alternate separators, and symlinked segments, whereas comparing the resolved path
    /// against the resolved root is decided by the same path logic the filesystem itself will use.
    /// The root is suffixed with a separator before comparison so a sibling directory whose name
    /// merely starts with the root's (<c>/data/store-evil</c> against <c>/data/store</c>) cannot pass.
    /// </summary>
    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A storage key is required.", nameof(key));
        }

        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException($"Storage key '{key}' must be relative.", nameof(key));
        }

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(rootWithSeparator, key));

        if (!resolved.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected storage key '{Key}': it resolves outside the storage root.", key);
            throw new ArgumentException($"Storage key '{key}' resolves outside the storage root.", nameof(key));
        }

        return resolved;
    }
}
