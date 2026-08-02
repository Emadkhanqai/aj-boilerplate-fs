namespace AjBoilerplate.Application.Identity;

/// <summary>
/// What a caller holding a given set of roles is allowed to do. This is the ONE source of truth the
/// server's authorization policies resolve through, so a policy decision and any capability flags
/// the API hands the client can never diverge — the classic failure being a UI that hides a button
/// the server would have allowed, or shows one it would have denied.
/// </summary>
/// <param name="CanRead">May read ordinary records.</param>
/// <param name="CanWrite">May create, update, or delete ordinary records.</param>
/// <param name="CanAdminister">May reach administrative surfaces (operational dashboards, config).</param>
public sealed record RoleCapabilities(bool CanRead, bool CanWrite, bool CanAdminister)
{
    private static readonly RoleCapabilities None = new(CanRead: false, CanWrite: false, CanAdminister: false);

    /// <summary>The capabilities granted by <paramref name="roles"/>. Unrecognised roles grant
    /// nothing — this fails closed.</summary>
    public static RoleCapabilities For(IEnumerable<string> roles) => ApplicationRoles.Effective(roles) switch
    {
        ApplicationRoles.Admin => new RoleCapabilities(CanRead: true, CanWrite: true, CanAdminister: true),
        ApplicationRoles.Editor => new RoleCapabilities(CanRead: true, CanWrite: true, CanAdminister: false),
        ApplicationRoles.Viewer => new RoleCapabilities(CanRead: true, CanWrite: false, CanAdminister: false),
        _ => None,
    };
}
