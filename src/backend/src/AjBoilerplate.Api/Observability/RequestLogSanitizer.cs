using System.Text.RegularExpressions;

namespace AjBoilerplate.Api.Observability;

/// <summary>
/// Redacts credentials out of request paths before they reach the request-logging middleware.
///
/// This matters because <c>UseSerilogRequestLogging()</c>'s default message template logs the full
/// <c>RequestPath</c> at Information level on EVERY request. Any credential that travels in the URL
/// — a password-reset token, a signed download link's signature, an OAuth <c>code</c> — is therefore
/// written verbatim into the application log, where it typically outlives its own expiry and is
/// visible to anyone with log access. That is a real credential leak, and it is invisible in
/// testing because the response itself looks perfectly normal.
///
/// Extracted as pure static functions (rather than inlined in the Program.cs lambda) so they can be
/// unit-tested directly, without standing up an in-memory log sink.
/// </summary>
public static class RequestLogSanitizer
{
    /// <summary>What replaces a redacted value. A fixed marker, not the empty string, so a log
    /// reader can tell "this was redacted" from "this was absent".</summary>
    public const string Redacted = "***REDACTED***";

    /// <summary>
    /// Query-string parameter names whose VALUES are never safe to log. Matched
    /// case-insensitively and as whole names.
    /// </summary>
    private static readonly string[] SensitiveQueryKeys =
    [
        "access_token", "token", "id_token", "refresh_token", "code",
        "secret", "client_secret", "api_key", "apikey", "key",
        "password", "signature", "sig",
    ];

    // Values are matched up to the next '&' or '#'. Bounded by a timeout: a request path is
    // attacker-controlled input and a catastrophically backtracking match must never hang a request.
    private static readonly Regex SensitiveQueryValuePattern = new(
        $"(?<=(?:^|[?&])(?:{string.Join('|', SensitiveQueryKeys)})=)[^&#]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Returns <paramref name="pathAndQuery"/> with the value of every sensitive query parameter
    /// replaced by <see cref="Redacted"/>. Everything else — the path, the parameter names, and any
    /// non-sensitive value — is returned unchanged, so the log stays useful.
    /// </summary>
    public static string Sanitize(string? pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery))
        {
            return string.Empty;
        }

        return SensitiveQueryValuePattern.Replace(pathAndQuery, Redacted);
    }

    /// <summary>
    /// Returns <paramref name="path"/> with the single segment immediately following
    /// <paramref name="prefix"/> replaced by <see cref="Redacted"/>.
    ///
    /// Use this when a route carries a bearer credential IN THE PATH rather than the query string —
    /// e.g. an anonymous magic-link surface routed at <c>/api/v1/invitations/{token}/...</c>. Compose
    /// it with <see cref="Sanitize"/> in the Program.cs enricher for each such route prefix; there
    /// are none in the sample slice, which is why nothing calls it yet.
    /// </summary>
    public static string RedactSegmentAfter(string? path, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = prefix.EndsWith('/') ? prefix : prefix + "/";
        var index = path.IndexOf(normalized, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return path;
        }

        var segmentStart = index + normalized.Length;
        if (segmentStart >= path.Length)
        {
            return path;
        }

        var segmentEnd = path.IndexOf('/', segmentStart);
        return segmentEnd < 0
            ? string.Concat(path.AsSpan(0, segmentStart), Redacted)
            : string.Concat(path.AsSpan(0, segmentStart), Redacted, path.AsSpan(segmentEnd));
    }
}
