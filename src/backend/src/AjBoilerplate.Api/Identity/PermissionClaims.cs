using System.Security.Claims;
using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Where a caller's <see cref="Permissions"/> live on the principal, and the only way an authorization
/// policy asks about one.
///
/// <para>
/// <b>A dedicated claim type, deliberately not <see cref="ClaimTypes.Role"/>.</b> Seats and
/// permissions arrive from Keycloak in the SAME flat <c>roles</c> claim, so the extraction step is the
/// one place they can still be told apart, and it is where they are separated. A permission written to
/// <see cref="ClaimTypes.Role"/> would be fed to <see cref="ApplicationRoles.Effective"/>, which decides
/// the capability tier and what <c>/api/v1/me</c> says the caller IS. A permission answers one policy
/// question and nothing else.
/// </para>
///
/// <para>
/// The raw permission strings stay on <see cref="ClaimTypes.Role"/> too — JwtBearer's inbound mapping
/// put them there and the transformation only ever adds claims — so this is a second, clean copy, not a
/// move. That is harmless because every seat consumer canonicalises through <see cref="ApplicationRoles"/>,
/// which drops anything that is not a seat.
/// </para>
///
/// <para>
/// The claim type is a URI in this project's own namespace, not a bare word: a short name like
/// <c>permission</c> would share a flat space with whatever an upstream IdP emits in a shared realm,
/// and a collision there would be a silent grant.
/// </para>
/// </summary>
public static class PermissionClaims
{
    /// <summary>The claim type carrying one canonical <see cref="Permissions"/> value per claim.</summary>
    public const string ClaimType = "https://schemas.aj-boilerplate/claims/permission";

    /// <summary>True when <paramref name="user"/> holds <paramref name="canonicalPermission"/>, which
    /// must already be a <see cref="Permissions"/> constant. The claims on the principal were
    /// canonicalised at extraction, so an ordinal comparison is the right one.</summary>
    public static bool Holds(ClaimsPrincipal user, string canonicalPermission) =>
        user.HasClaim(ClaimType, canonicalPermission);
}
