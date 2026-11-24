using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AjBoilerplate.Auth.Events;

/// <summary>
/// Extracts the Keycloak user id from a Pub/Sub push envelope. Keycloak does not publish to Pub/Sub on its own: an event
/// listener (or a log sink) has to, so this accepts the shapes such a publisher realistically emits: a user event (<c>userId</c>), an admin event (<c>resourcePath</c> =
/// <c>users/{id}</c>, optionally <c>…/role-mappings/…</c>), or an explicit <c>sub</c>.
/// </summary>
internal static partial class UserChangeMessage
{
    /// <returns>The Keycloak user id (<c>sub</c>), or <c>null</c> when the message is not about a user.</returns>
    public static string? TryGetSubject(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("message", out var message)
            || !message.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!)));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in (string[])["sub", "userId"])
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && IsUserId(value.GetString()))
                {
                    return value.GetString();
                }
            }

            if (root.TryGetProperty("resourcePath", out var path) && path.ValueKind == JsonValueKind.String)
            {
                var match = UserPath().Match(path.GetString()!);
                if (match.Success)
                {
                    return match.Groups["id"].Value;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool IsUserId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    [GeneratedRegex(@"^users/(?<id>[A-Za-z0-9_-]{1,64})(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex UserPath();
}
