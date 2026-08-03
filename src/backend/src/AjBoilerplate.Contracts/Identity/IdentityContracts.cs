namespace AjBoilerplate.Contracts.Identity;

/// <summary>
/// What the CURRENT caller is and what the server will let them do — the response of
/// <c>GET /api/v1/me</c>, and the only place a client is told about its own permissions.
///
/// Every field is derived server-side from the authenticated principal. Nothing here is echoed back
/// from a request: a client cannot ask to be an administrator any more than it can ask to be someone
/// else. That is the whole point of the endpoint — the browser holds a token that says who it is,
/// and the server answers what that means.
/// </summary>
/// <param name="UserId">The identity provider's subject identifier for this caller.</param>
/// <param name="DisplayName">The caller's human-readable name, for chrome that greets them.</param>
/// <param name="Email">The caller's email address; empty when the token carries none.</param>
/// <param name="Roles">
/// Every canonical role the caller holds — <c>Admin</c>, <c>Editor</c>, <c>Viewer</c> — never the
/// authorization server's raw lowercase keys, and never a role this application does not recognise.
/// Empty for an authenticated caller who has been issued no recognised role at all.
/// </param>
/// <param name="Capabilities">
/// What those roles permit. Gate UI affordances on THESE, never on <paramref name="Roles"/>: a role
/// is an input to the permission model, and a client that re-derives permissions from role names has
/// quietly forked the model.
/// </param>
public sealed record UserProfileResponse(
    string UserId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    UserCapabilitiesResponse Capabilities);

/// <summary>
/// The permission flags a client may gate its UI on, resolved server-side.
///
/// These are a UX affordance and NEVER a security boundary. The server authorizes every request
/// independently; hiding a button here protects nothing, and a caller who invokes the endpoint
/// directly still gets a 403. They exist so the UI can avoid offering an action that would fail —
/// not so the UI can decide anything.
/// </summary>
/// <param name="CanView">May read ordinary records.</param>
/// <param name="CanCreate">May create ordinary records.</param>
/// <param name="CanEdit">May modify ordinary records.</param>
/// <param name="CanDelete">May delete ordinary records.</param>
/// <param name="CanAdminister">May reach administrative surfaces (operational dashboards, config).</param>
public sealed record UserCapabilitiesResponse(
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    bool CanAdminister);
