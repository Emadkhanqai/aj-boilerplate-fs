using System.Text;
using AjBoilerplate.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.UnitTests.Storage;

/// <summary>
/// The local file store. Half of these are ordinary round-trip assertions; the other half are the
/// security boundary — a storage key is routinely built from user input, so a key that escapes the
/// storage root is the expected attack rather than a hypothetical one.
/// </summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ajb-storage-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_saved_file_reads_back_with_its_content_and_media_type()
    {
        var storage = NewStorage();

        await storage.SaveAsync("docs/report.txt", Content("hello"), "text/plain", CancellationToken.None);

        using var stored = await storage.OpenReadAsync("docs/report.txt", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("text/plain", stored!.ContentType);
        Assert.Equal(5, stored.Length);
        Assert.Equal("hello", await new StreamReader(stored.Content).ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_file_reads_back_as_null_rather_than_throwing()
    {
        // A key from a stale link is an ordinary, foreseeable state — the same contract
        // ISecretsProvider gives a missing secret.
        Assert.Null(await NewStorage().OpenReadAsync("nope.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Saving_over_an_existing_key_replaces_it()
    {
        var storage = NewStorage();
        await storage.SaveAsync("a.txt", Content("first"), "text/plain", CancellationToken.None);

        await storage.SaveAsync("a.txt", Content("second"), "text/plain", CancellationToken.None);

        using var stored = await storage.OpenReadAsync("a.txt", CancellationToken.None);
        Assert.Equal("second", await new StreamReader(stored!.Content).ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Existence_and_deletion_report_honestly()
    {
        var storage = NewStorage();
        await storage.SaveAsync("a.txt", Content("x"), "text/plain", CancellationToken.None);

        Assert.True(await storage.ExistsAsync("a.txt", CancellationToken.None));
        Assert.True(await storage.DeleteAsync("a.txt", CancellationToken.None));
        Assert.False(await storage.ExistsAsync("a.txt", CancellationToken.None));

        // Deleting twice is the normal shape of a retry, not a fault.
        Assert.False(await storage.DeleteAsync("a.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_file_takes_its_media_type_with_it()
    {
        var storage = NewStorage();
        await storage.SaveAsync("a.txt", Content("x"), "text/plain", CancellationToken.None);
        await storage.DeleteAsync("a.txt", CancellationToken.None);

        // A stranded sidecar would make a later save/read pair report a stale media type.
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("docs/../../escaped.txt")]
    [InlineData("docs/../../../etc/passwd")]
    [InlineData("a/b/../../../outside.txt")]
    public async Task A_key_that_escapes_the_storage_root_is_refused(string key)
    {
        // THE test on this class. Rejecting rather than normalising is deliberate: silently rewriting
        // a traversal attempt into a "safe" path hides a caller bug that is worth surfacing.
        var storage = NewStorage();

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.SaveAsync(key, Content("x"), "text/plain", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ExistsAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task An_absolute_key_is_refused()
    {
        var storage = NewStorage();

        // A UNIQUE path per run, not a fixed "absolute.txt". The assertion below is "the guard did not
        // let this file be created", so a fixed name in the shared temp directory would be satisfied
        // by a leftover from any earlier run — including a run where the guard was deliberately
        // removed to prove this test fails without it. That is a test that reports on the state of
        // /tmp rather than on the code.
        var absolute = Path.Combine(Path.GetTempPath(), $"ajb-absolute-{Guid.NewGuid():N}.txt");

        // Path.Combine would DISCARD the root entirely and write wherever the key points, which is the
        // subtle version of the traversal bug — no ".." in sight.
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.SaveAsync(absolute, Content("x"), "text/plain", CancellationToken.None));
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task A_sibling_directory_sharing_the_roots_name_prefix_is_refused()
    {
        // "/tmp/ajb-storage-xyz-evil" starts with "/tmp/ajb-storage-xyz" as a STRING but is a
        // different directory. A prefix check without the trailing separator would let this through.
        var storage = NewStorage();

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.SaveAsync(
                $"../{Path.GetFileName(_root)}-evil/x.txt", Content("x"), "text/plain", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_key_is_refused(string key) =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewStorage().SaveAsync(key, Content("x"), "text/plain", CancellationToken.None));

    [Fact]
    public async Task Nesting_is_created_on_demand()
    {
        var storage = NewStorage();

        await storage.SaveAsync("a/b/c/deep.txt", Content("x"), "text/plain", CancellationToken.None);

        Assert.True(await storage.ExistsAsync("a/b/c/deep.txt", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream Content(string value) => new(Encoding.UTF8.GetBytes(value));

    private LocalFileStorage NewStorage() => new(
        Options.Create(new FileStorageOptions { LocalRoot = _root }),
        NullLogger<LocalFileStorage>.Instance);
}
