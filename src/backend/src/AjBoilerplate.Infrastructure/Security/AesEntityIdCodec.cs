using System.Security.Cryptography;
using System.Text;
using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Security;

/// <summary>
/// <see cref="IEntityIdCodec"/> over AES-256 in CBC mode with a FIXED, key-derived IV (deliberately
/// not random — determinism, so the same id always yields the same token, is a hard requirement
/// here; see <see cref="IEntityIdCodec"/>). The 4-byte big-endian representation of the id is placed
/// in the last 4 bytes of a single 16-byte AES block (the leading 12 bytes are always zero), so
/// exactly one block is ever encrypted — a fixed IV is safe for CBC precisely because no message
/// here is ever more than one block, so there is no cross-block IV-reuse exposure. AES-GCM (or any
/// AEAD mode) is deliberately NOT used: a fixed nonce/IV under GCM is catastrophic for its
/// authentication guarantee, whereas plain CBC has no such failure mode. In place of a dedicated
/// MAC, the always-zero leading 12 bytes double as a lightweight integrity check on decode (see
/// <see cref="TryDecode"/>) — a tampered ciphertext decrypts to effectively-random bytes there, so
/// it is rejected with overwhelming probability (2^-96).
///
/// Key and IV are both derived from the single configured secret via HKDF-SHA256 with distinct
/// domain-separation "info" strings, so one configured secret yields two independent-looking values
/// without needing two separately-managed config keys.
/// </summary>
public sealed class AesEntityIdCodec : IEntityIdCodec
{
    private const int BlockSizeBytes = 16;
    private const int KeySizeBytes = 32; // AES-256 — exceeds the 128-bit minimum.
    private const int IdSizeBytes = sizeof(int);
    private const int PaddingSizeBytes = BlockSizeBytes - IdSizeBytes;

    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("AjBoilerplate.EntityIdCodec.Aes256Key.v1");
    private static readonly byte[] IvInfo = Encoding.UTF8.GetBytes("AjBoilerplate.EntityIdCodec.AesFixedIv.v1");

    private readonly byte[] _key;
    private readonly byte[] _iv;

    public AesEntityIdCodec(IOptions<EntityIdObfuscationOptions> options)
    {
        var secret = options.Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "EntityIdObfuscation:EncryptionKey is not configured. Every deployed environment must " +
                "supply it from the cloud secret store or the environment — see EntityIdObfuscationOptions. " +
                "Do not reuse another component's key.");
        }

        var ikm = Encoding.UTF8.GetBytes(secret);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySizeBytes, info: KeyInfo);
        _iv = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, BlockSizeBytes, info: IvInfo);
    }

    public string Encode(int id)
    {
        var block = new byte[BlockSizeBytes];
        WriteBigEndian(id, block);

        using var aes = CreateAes();
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(block, 0, block.Length);
        return Base64UrlEncode(cipher);
    }

    public bool TryDecode(string token, out int id)
    {
        id = 0;

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] cipher;
        try
        {
            cipher = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (cipher.Length != BlockSizeBytes)
        {
            return false;
        }

        byte[] block;
        try
        {
            using var aes = CreateAes();
            using var decryptor = aes.CreateDecryptor();
            block = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }
        catch (CryptographicException)
        {
            return false;
        }

        for (var i = 0; i < PaddingSizeBytes; i++)
        {
            if (block[i] != 0)
            {
                return false;
            }
        }

        id = ReadBigEndian(block);
        return true;
    }

    private Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    private static void WriteBigEndian(int value, byte[] block)
    {
        block[PaddingSizeBytes] = (byte)(value >> 24);
        block[PaddingSizeBytes + 1] = (byte)(value >> 16);
        block[PaddingSizeBytes + 2] = (byte)(value >> 8);
        block[PaddingSizeBytes + 3] = (byte)value;
    }

    private static int ReadBigEndian(byte[] block) =>
        (block[PaddingSizeBytes] << 24) | (block[PaddingSizeBytes + 1] << 16) |
        (block[PaddingSizeBytes + 2] << 8) | block[PaddingSizeBytes + 3];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string token)
    {
        var normalized = token.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            case 1:
                throw new FormatException("Invalid Base64Url token length.");
        }

        return Convert.FromBase64String(normalized);
    }
}
