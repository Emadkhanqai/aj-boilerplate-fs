using System.Security.Claims;
using AjBoilerplate.Application.Identity;
using Microsoft.AspNetCore.Authorization;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// The union every narrow policy is built from: the caller holds one of <c>seats</c>, OR holds
/// <c>permission</c>. A caller holding both is granted once — this is an <c>||</c>, not a tally.
///
/// <para>
/// The seat half is <see cref="ApplicationRoles.Canonical"/> membership over the caller's FULL set of
/// <see cref="ClaimTypes.Role"/> claims, not <see cref="ApplicationRoles.Effective"/>'s single winner.
/// The permission half reads a different claim type entirely (<see cref="PermissionClaims"/>), so no
/// permission can ever be counted as a seat.
/// </para>
///
/// <para>
/// The three coarse policies (<see cref="Policies.ReadAccess"/>, <see cref="Policies.WriteAccess"/>,
/// <see cref="Policies.AdminAccess"/>) do NOT use this: they resolve through
/// <see cref="RoleCapabilities.For(IEnumerable{string}, IEnumerable{string})"/>, the function
/// <c>/api/v1/me</c> also calls, so the server's decision and the client's flags cannot drift. Use this
/// for a narrower policy that names its own seats and its own permission.
/// </para>
/// </summary>
public static class SeatOrPermission
{
    public static bool Holds(ClaimsPrincipal user, string permission, params string[] seats)
    {
        var held = user.FindAll(ClaimTypes.Role)
            .Select(c => ApplicationRoles.Canonical(c.Value))
            .Where(role => role is not null)
            .ToHashSet(StringComparer.Ordinal);

        var wanted = seats.Select(ApplicationRoles.Canonical).Where(role => role is not null);
        return wanted.Any(held.Contains) || PermissionClaims.Holds(user, permission);
    }

    /// <summary>Adds a policy that requires an authenticated caller who holds a seat OR the
    /// permission.</summary>
    public static AuthorizationPolicyBuilder RequireSeatOrPermission(
        this AuthorizationPolicyBuilder builder, string permission, params string[] seats) =>
        builder
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => Holds(ctx.User, permission, seats));
}
