using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AjBoilerplate.Auth.Security;

public static class SessionIds
{
    private const int RandomBytes = 32;

    private const int EncodedLength = 43;

    public static string NewRandom() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    public static string StorageKey(string rawValue) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));

    public static string CodeChallenge(string codeVerifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != EncodedLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HashesEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
