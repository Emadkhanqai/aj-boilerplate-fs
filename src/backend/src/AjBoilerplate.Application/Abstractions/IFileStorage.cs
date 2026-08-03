namespace AjBoilerplate.Application.Abstractions;

/// <summary>A stored file's content and metadata, as handed back by <see cref="IFileStorage"/>.</summary>
/// <param name="Content">The file's bytes. The caller owns the stream and must dispose it.</param>
/// <param name="ContentType">The media type recorded when the file was saved.</param>
/// <param name="Length">The file's size in bytes.</param>
public sealed record StoredFile(Stream Content, string ContentType, long Length) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Stores and retrieves binary content by key. The counterpart to <see cref="IEmailSender"/>: an
/// application flow that saves an upload or produces a report is storage-agnostic, and the transport
/// is chosen by configuration rather than by the code doing the work.
///
/// <para>
/// <b>Keys are opaque, relative, and forward-slash separated</b> — <c>invoices/2026/03/inv-1.pdf</c>.
/// They are NOT filesystem paths, even when the local implementation happens to make them into ones.
/// A key that escapes its container (a leading <c>/</c>, a drive letter, any <c>..</c> segment) is
/// rejected rather than normalised: a caller that builds a key from user input is the realistic case,
/// and quietly rewriting a traversal attempt into a "safe" path hides the bug instead of surfacing
/// it. Implementations MUST enforce this — see <c>LocalFileStorage</c>.
/// </para>
///
/// <para>
/// <b>Deliberately no SAS/signed-URL method.</b> Handing a client a time-limited direct URL is the
/// right pattern for large files, but its semantics differ enough between providers (expiry
/// granularity, permission model, whether the URL can be revoked) that a single abstraction over it
/// would either leak the provider's model or lie about it. Add it as a provider-specific port when
/// you need it, rather than pretending it is portable.
/// </para>
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Writes <paramref name="content"/> at <paramref name="key"/>, replacing anything already there.
    /// Returns the key, so a caller that let the implementation normalise one can record what was
    /// actually used.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty or escapes its container.</exception>
    Task<string> SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// The file at <paramref name="key"/>, or null when there is none. A missing file is an ordinary,
    /// foreseeable state — a record whose attachment was cleaned up, a key from a stale link — so it
    /// surfaces as null rather than as a provider-specific exception, exactly as
    /// <see cref="ISecretsProvider.GetSecretAsync"/> treats a missing secret. Genuine failures (an
    /// unreachable store, a denied permission) still throw.
    /// </summary>
    Task<StoredFile?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>True when a file exists at <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the file at <paramref name="key"/>. Returns false when there was nothing to delete, so
    /// a retried cleanup is a success rather than an error — deleting twice is the normal shape of a
    /// retry, not a fault.
    /// </summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
