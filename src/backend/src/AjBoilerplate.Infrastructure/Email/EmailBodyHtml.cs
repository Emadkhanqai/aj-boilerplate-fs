using System.Net;
using System.Text.RegularExpressions;

namespace AjBoilerplate.Infrastructure.Email;

/// <summary>
/// Converts a PLAIN-TEXT email body into minimal, safe HTML.
///
/// Bodies are authored as plain text everywhere in this codebase. Sending them as-is leaves bare
/// URLs unclickable in clients that do not auto-link, so they are converted here instead of every
/// author hand-writing HTML — which is also what keeps the escaping in one place: the whole body is
/// HTML-encoded FIRST, so no caller-supplied text can inject markup, and only then are the
/// already-encoded http/https URLs turned into anchors.
/// </summary>
public static class EmailBodyHtml
{
    // Runs against the HTML-ENCODED text, so it must not treat an entity such as &amp; as part of a
    // URL. Bounded by a timeout: the body is caller-influenced input and a catastrophically
    // backtracking match must never hang a request.
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The HTML body for <paramref name="plainText"/>.</summary>
    public static string From(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var encoded = WebUtility.HtmlEncode(plainText);
        var linked = UrlPattern.Replace(encoded, match => $"<a href=\"{match.Value}\">{match.Value}</a>");
        var withBreaks = linked.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);

        return $"<html><body style=\"font-family:sans-serif;font-size:14px\">{withBreaks}</body></html>";
    }
}
