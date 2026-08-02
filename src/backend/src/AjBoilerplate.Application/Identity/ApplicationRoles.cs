namespace AjBoilerplate.Application.Identity;

/// <summary>
/// The application's role vocabulary and the single canonicalisation table for it.
///
/// The authorization server (Keycloak) is the source of truth for who holds which role, but it
/// emits lowercase realm/client role KEYS (<c>admin</c>), while everything user-facing shows a
/// display NAME (<c>Admin</c>). Both spellings genuinely arrive in a single token — the JWT bearer
/// handler projects the raw key straight into a role claim, and the claims transformation appends
/// the display name — so every consumer must canonicalise defensively rather than assume one shape.
/// Keeping ONE table here, in the Application layer, is what stops the Api-layer transformation and
/// the Application-layer policies from drifting apart; two copies would be free to disagree, and
/// that disagreement is invisible on the server until someone's list silently comes back empty.
///
/// Extend <see cref="Canonical"/>'s table when you add a role. Anything unmapped returns null and is
/// dropped: a role is never granted implicitly.
/// </summary>
public static class ApplicationRoles
{
    /// <summary>Full control, including any administrative surface.</summary>
    public const string Admin = "Admin";

    /// <summary>May read and write ordinary records.</summary>
    public const string Editor = "Editor";

    /// <summary>Read-only.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Most-authoritative first. <see cref="Effective"/> picks the caller's highest seat
    /// from this order, so a user holding several roles gets the union of their capabilities via one
    /// deterministic winner rather than whichever claim happened to be enumerated first.</summary>
    private static readonly string[] ByAuthorityDescending = [Admin, Editor, Viewer];

    /// <summary>
    /// The canonical display name for <paramref name="role"/>, accepting either the authorization
    /// server's lowercase key or the display name itself. Returns null for anything unrecognised.
    /// </summary>
    public static string? Canonical(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "admin" => Admin,
        "editor" => Editor,
        "viewer" => Viewer,
        _ => null,
    };

    /// <summary>
    /// The single highest-authority role among <paramref name="roles"/>, canonicalised, or null when
    /// none of them is recognised.
    /// </summary>
    public static string? Effective(IEnumerable<string> roles)
    {
        var canonical = roles
            .Select(Canonical)
            .Where(role => role is not null)
            .Select(role => role!)
            .ToHashSet(StringComparer.Ordinal);

        return Array.Find(ByAuthorityDescending, canonical.Contains);
    }
}
