using System.Security.Cryptography;
using System.Text;

namespace AjBoilerplate.Domain.Common;

/// <summary>
/// SHA-256 hashing for values that must never be persisted or logged raw — IP addresses, user
/// agents, and any other directly-identifying value an audit row still needs to correlate on.
/// Callers hash the raw value at the boundary before it ever reaches a domain entity; entities only
/// ever hold the hash.
/// </summary>
public static class PrivacyHash
{
    /// <summary>Returns the lowercase hex SHA-256 of <paramref name="rawValue"/>, or null when the
    /// input is null/whitespace (nothing to hash — e.g. no IP available for a given request).</summary>
    public static string? OfOrNull(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
