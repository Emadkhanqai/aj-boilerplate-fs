using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Identity;

namespace AjBoilerplate.Auth.Security;

public sealed class RolesVersion
{
    internal const string Purpose = "auth/roles-version";

    private const int Length = 16;

    private readonly byte[] _key;

    public RolesVersion(SessionTokenProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _key = protector.DeriveSubkey(Purpose);
    }

    public string? For(string? jwt, string clientId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var claims = JwtClaims.TryParse(jwt);
        if (claims is null)
        {
            return null;
        }

        var roles = claims.ClientRoles(clientId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (roles.Count == 0)
        {
            return null;
        }

        var input = JsonSerializer.SerializeToUtf8Bytes((string[])[clientId, .. roles]);
        var mac = HMACSHA256.HashData(_key, input);
        return Convert.ToHexStringLower(mac)[..Length];
    }
}
