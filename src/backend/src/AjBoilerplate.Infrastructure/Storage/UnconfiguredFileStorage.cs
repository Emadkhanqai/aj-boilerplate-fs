using AjBoilerplate.Application.Abstractions;

namespace AjBoilerplate.Infrastructure.Storage;

/// <summary>
/// The <see cref="IFileStorage"/> registered outside Development when no storage is configured. Every
/// method throws with the steps needed to configure one.
///
/// <para>
/// <b>It throws rather than no-opping, and that is the whole point.</b> <c>NullSecretsProvider</c> can
/// honestly return null because <see cref="ISecretsProvider"/> defines "no such secret" as a normal
/// answer. Storage has no such answer: a save that quietly discarded a file, or a read that pretended
/// one was missing, would look exactly like success until someone went looking for a document that
/// was never written. A boilerplate that faked this would be a data-loss bug, not a convenience.
/// </para>
///
/// <para>
/// It exists at all — instead of simply not registering <see cref="IFileStorage"/> — so the failure is
/// a clear message about storage configuration rather than a DI resolution error naming a service the
/// reader has to go and find. Registration is also deferred to first use, so an application that never
/// stores a file is unaffected.
/// </para>
/// </summary>
public sealed class UnconfiguredFileStorage : IFileStorage
{
    private readonly string _setting;
    private readonly string _guidance;

    public UnconfiguredFileStorage(string setting, string guidance)
    {
        _setting = setting;
        _guidance = guidance;
    }

    public Task<string> SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        throw Unconfigured();

    public Task<StoredFile?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        throw Unconfigured();

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        throw Unconfigured();

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw Unconfigured();

    private InvalidOperationException Unconfigured() => new(
        $"File storage is not configured. Set {_setting} and provide an IFileStorage implementation for it. "
        + _guidance
        + " This boilerplate ships only a local-disk implementation, which is process-local and is therefore "
        + "not offered outside Development.");
}
