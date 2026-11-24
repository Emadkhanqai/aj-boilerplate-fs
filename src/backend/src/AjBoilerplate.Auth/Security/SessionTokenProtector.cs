using System.Security.Cryptography;
using System.Text;

namespace AjBoilerplate.Auth.Security;

public sealed class SessionTokenProtector
{
    private const string Version = "v1";
    private const int KeySize = 32;
    private const int NonceSize = 12;   // AES-GCM standard, and what the .NET API expects
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SessionTokenProtector(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException(
                "Auth:Session:EncryptionKey is required (it protects the session tokens in Redis and SQL). Provision the " +
                "a secret holding 32 random bytes, base64 (see SessionTokenProtector.GenerateKey) and reference it from the service.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Auth:Session:EncryptionKey is not valid base64.", ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"Auth:Session:EncryptionKey must decode to {KeySize} bytes; got {key.Length}.");
        }

        _key = key;
    }

    internal byte[] DeriveSubkey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, KeySize, salt: null, info: Encoding.UTF8.GetBytes(purpose));

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize));

    public string? Protect(string? plaintext)
    {
        if (plaintext is null)
        {
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[bytes.Length + TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, bytes, cipher.AsSpan(0, bytes.Length), cipher.AsSpan(bytes.Length, TagSize));

        return $"{Version}.{Base64Url(nonce)}.{Base64Url(cipher)}";
    }

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Version + ".", StringComparison.Ordinal);

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        var parts = protectedValue.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var nonce = FromBase64Url(parts[1]);
            var cipher = FromBase64Url(parts[2]);
            if (nonce.Length != NonceSize || cipher.Length < TagSize)
            {
                return null;
            }

            var plain = new byte[cipher.Length - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
