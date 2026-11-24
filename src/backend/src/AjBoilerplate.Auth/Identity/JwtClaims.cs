using System.Buffers.Text;
using System.Text.Json;

namespace AjBoilerplate.Auth.Identity;

public sealed class JwtClaims
{
    private JwtClaims(string subject, DateTimeOffset expiresAt, string? authorizedParty, JsonElement payload)
    {
        Subject = subject;
        ExpiresAt = expiresAt;
        AuthorizedParty = authorizedParty;
        _payload = payload;
    }

    private readonly JsonElement _payload;

    public string Subject { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string? AuthorizedParty { get; }

    public static JwtClaims? TryParse(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(parts[1]);
            using var document = JsonDocument.Parse(bytes);
            var payload = document.RootElement.Clone();

            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(sub.GetString())
                || !payload.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var expSeconds))
            {
                return null;
            }

            var authorizedParty = payload.TryGetProperty("azp", out var azp) && azp.ValueKind == JsonValueKind.String
                ? azp.GetString()
                : null;

            return new JwtClaims(
                sub.GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(expSeconds),
                string.IsNullOrEmpty(authorizedParty) ? null : authorizedParty,
                payload);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public bool IsForAnotherClient(string clientId) =>
        AuthorizedParty is not null && !string.Equals(AuthorizedParty, clientId, StringComparison.Ordinal);

    public IReadOnlyList<string> ClientRoles(string clientId)
    {
        if (string.Equals(AuthorizedParty, clientId, StringComparison.Ordinal)
            && _payload.TryGetProperty("roles", out var flat)
            && flat.ValueKind == JsonValueKind.Array)
        {
            var flatRoles = NonEmptyStrings(flat);
            if (flatRoles.Count > 0)
            {
                return flatRoles;
            }
        }

        if (_payload.TryGetProperty("resource_access", out var access)
            && access.ValueKind == JsonValueKind.Object
            && access.TryGetProperty(clientId, out var client)
            && client.ValueKind == JsonValueKind.Object
            && client.TryGetProperty("roles", out var roles)
            && roles.ValueKind == JsonValueKind.Array)
        {
            return NonEmptyStrings(roles);
        }

        return [];
    }

    private static List<string> NonEmptyStrings(JsonElement array) =>
        [.. array.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(r.GetString()))
            .Select(r => r.GetString()!)];
}
