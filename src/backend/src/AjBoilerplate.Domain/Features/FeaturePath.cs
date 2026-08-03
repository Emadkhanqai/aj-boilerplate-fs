namespace AjBoilerplate.Domain.Features;

/// <summary>
/// Canonicalises the URL path a client claims to be on, before it is ever compared against a
/// <see cref="FeatureAnnouncement"/>'s page list.
///
/// This is a SECURITY control, not formatting. The page list is matched by prefix, and a raw
/// <c>StartsWith</c> comparison against an unresolved path is trivially defeated: a caller sending
/// <c>/reports/../admin</c> literally starts with <c>/reports</c>, so an announcement scoped to the
/// reports area would fire on what actually resolves — in the browser, and in every other consumer
/// of that URL — to <c>/admin</c>. Resolving the <c>.</c> and <c>..</c> segments HERE means the
/// prefix comparison downstream can only ever see the resolved path.
/// </summary>
public static class FeaturePath
{
    /// <summary>The path returned for a missing, blank, or fully-collapsed input.</summary>
    public const string Root = "/";

    private static readonly char[] QueryOrFragment = ['?', '#'];

    /// <summary>
    /// The absolute, canonical path of <paramref name="path"/>: query string and fragment removed,
    /// <c>.</c> and <c>..</c> segments resolved, and empty segments (repeated or trailing slashes)
    /// collapsed. <c>..</c> can never escape above the root — a stack that is already empty simply
    /// stays empty, so the result is always rooted.
    ///
    /// Idempotent: normalising an already-normalised path returns it unchanged, which is what lets
    /// <see cref="FeatureAnnouncement.Targets"/> apply it defensively without a caller having to
    /// remember to.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }

        var trimmed = path.Trim();

        // Only the path participates in page targeting; a query string or fragment is caller data
        // that must not influence which prefix matches.
        var cut = trimmed.IndexOfAny(QueryOrFragment);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        var segments = new List<string>();
        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return Root + string.Join('/', segments);
    }
}
