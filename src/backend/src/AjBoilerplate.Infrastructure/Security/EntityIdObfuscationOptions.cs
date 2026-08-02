namespace AjBoilerplate.Infrastructure.Security;

/// <summary>
/// Bound from the <c>EntityIdObfuscation</c> config section. <see cref="EncryptionKey"/> is the
/// secret <see cref="AesEntityIdCodec"/> derives its AES key and IV from — sourced from the cloud
/// secret store or the environment, never from a committed file — and it must be a DISTINCT secret
/// from every other keyed-crypto value the application holds, so a compromise of one can never be
/// used to forge another.
/// </summary>
public sealed class EntityIdObfuscationOptions
{
    public const string SectionName = "EntityIdObfuscation";

    public string EncryptionKey { get; set; } = string.Empty;
}
