namespace AjBoilerplate.Auth.Configuration;

/// <summary>Host-name normalisation shared by configuration and request handling.</summary>
public static class HostName
{
    /// <summary>
    /// Normalises a header value to a bare, lower-case host: takes the first entry of a
    /// comma-separated list (what a proxy chain produces), drops a port, rejects anything else.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var first = value.Split(',', 2)[0].Trim();
        if (first.Length == 0 || first.Contains('/', StringComparison.Ordinal) || first.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        // Uri parses "host" and "host:port"; IPv6 literals are not a portal host and are refused.
        if (!Uri.TryCreate("https://" + first, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
        {
            return null;
        }

        return uri.IdnHost.ToLowerInvariant();
    }

    /// <summary>True when <paramref name="value"/> is already a bare DNS host (no port).</summary>
    public static bool IsValid(string value) =>
        !value.Contains(':', StringComparison.Ordinal) && string.Equals(Normalize(value), value, StringComparison.OrdinalIgnoreCase);
}
