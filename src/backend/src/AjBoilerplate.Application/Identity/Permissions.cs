namespace AjBoilerplate.Application.Identity;

/// <summary>
/// The shipped example permission vocabulary: <c>resource:action</c> strings a Keycloak client role
/// can carry alongside (or instead of) a seat. A permission answers one authorization question; it
/// is never a seat and never decides who the caller IS (<c>/api/v1/me</c> roles, capability tier).
///
/// <para>
/// Add a project's own permission here AND to <see cref="All"/>. A string this class does not know
/// is dropped when the token's roles are projected, so a realm typo or another application's role
/// key can never be granted implicitly.
/// </para>
/// </summary>
public static class Permissions
{
    public const string ItemsRead = "items:read";

    public const string ItemsWrite = "items:write";

    public const string AdminManage = "admin:manage";

    public static IReadOnlyList<string> All { get; } = [ItemsRead, ItemsWrite, AdminManage];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>The canonical (lower-case) permission for <paramref name="permission"/>, or null when
    /// it is not in <see cref="All"/>.</summary>
    public static string? Canonical(string? permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
        {
            return null;
        }

        var normalised = permission.Trim().ToLowerInvariant();
        return Known.Contains(normalised) ? normalised : null;
    }

    public static bool IsPermission(string? permission) => Canonical(permission) is not null;
}
