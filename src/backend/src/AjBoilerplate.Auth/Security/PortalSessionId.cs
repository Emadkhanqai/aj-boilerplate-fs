using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AjBoilerplate.Auth.Security;

public sealed class PortalSessionId
{
    internal const string Purpose = "auth/portal-session-id";

    private const int Length = 32;

    private readonly byte[] _key;

    public PortalSessionId(SessionTokenProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _key = protector.DeriveSubkey(Purpose);
    }

    public string For(string sessionStorageKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionStorageKey);
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(sessionStorageKey));
        return Convert.ToHexStringLower(mac)[..Length];
    }

    public static string FormatStartedAt(DateTimeOffset signedInAt) =>
        DateTimeOffset.FromUnixTimeSeconds(signedInAt.ToUnixTimeSeconds())
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
