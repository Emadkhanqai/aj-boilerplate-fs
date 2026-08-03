namespace AjBoilerplate.Application.Identity;

/// <summary>The current caller's identity and effective permissions, as the Application layer models
/// them. The Api maps this to the wire DTO.</summary>
/// <param name="UserId">The identity provider's subject identifier.</param>
/// <param name="DisplayName">The caller's human-readable name.</param>
/// <param name="Email">The caller's email address; empty when the token carries none.</param>
/// <param name="Roles">Every canonical role the caller holds. Empty when none is recognised.</param>
/// <param name="Capabilities">What those roles permit.</param>
public sealed record UserProfileDto(
    string UserId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    UserCapabilitiesDto Capabilities);

/// <summary>
/// The client-facing PROJECTION of <see cref="RoleCapabilities"/>.
///
/// <see cref="RoleCapabilities"/> has three flags because the server enforces exactly three policies
/// (<c>ReadAccess</c>, <c>WriteAccess</c>, <c>AdminAccess</c>). A client's UI reasons in finer terms
/// than that — a "New" button, an "Edit" button, a "Delete" button are three separate affordances —
/// so this shape names them separately. It does NOT invent authority the server does not grant:
/// <see cref="From"/> is the single, explicit mapping between the two, and every finer-grained flag
/// resolves from the coarse capability the server actually checks.
///
/// That direction matters. Projecting the server's real capabilities outward can only ever produce
/// flags the server would honour. Deriving them independently — a second table, a client-side rule —
/// is how a UI ends up hiding a button the server would have allowed, or offering one it would
/// refuse; the failure <see cref="RoleCapabilities"/>'s own summary names.
///
/// When you split <c>WriteAccess</c> into finer policies, add the flag to
/// <see cref="RoleCapabilities"/> FIRST, enforce it in <c>AuthenticationSetup</c>, and only then
/// change the mapping here. A flag that appears in this projection without a policy behind it is a
/// promise the server does not keep.
/// </summary>
/// <param name="CanView">May read ordinary records.</param>
/// <param name="CanCreate">May create ordinary records.</param>
/// <param name="CanEdit">May modify ordinary records.</param>
/// <param name="CanDelete">May delete ordinary records.</param>
/// <param name="CanAdminister">May reach administrative surfaces.</param>
public sealed record UserCapabilitiesDto(
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    bool CanAdminister)
{
    /// <summary>
    /// Projects the server's enforced capabilities onto the client-facing flags.
    ///
    /// Create, edit, and delete all resolve from <see cref="RoleCapabilities.CanWrite"/> because ONE
    /// policy — <c>WriteAccess</c> — guards all three on every controller that has them; the sample
    /// <c>ItemsController</c>'s POST, PUT, and DELETE carry exactly that attribute. Reporting
    /// <c>canDelete: false</c> to a caller the server would let delete would be a lie about the
    /// server's behaviour, not a tightening of it.
    /// </summary>
    public static UserCapabilitiesDto From(RoleCapabilities capabilities) => new(
        CanView: capabilities.CanRead,
        CanCreate: capabilities.CanWrite,
        CanEdit: capabilities.CanWrite,
        CanDelete: capabilities.CanWrite,
        CanAdminister: capabilities.CanAdminister);
}
