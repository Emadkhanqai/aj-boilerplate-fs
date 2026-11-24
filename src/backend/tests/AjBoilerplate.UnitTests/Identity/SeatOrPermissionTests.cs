using System.Security.Claims;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>The union a narrow policy is built from: a seat OR a permission, never a permission read
/// as a seat.</summary>
public sealed class SeatOrPermissionTests
{
    [Fact]
    public void A_listed_seat_satisfies_it() =>
        Assert.True(SeatOrPermission.Holds(Principal(Role("Editor")), Permissions.AdminManage, ApplicationRoles.Editor));

    [Fact]
    public void The_permission_satisfies_it_without_any_seat() =>
        Assert.True(SeatOrPermission.Holds(Principal(Permission(Permissions.AdminManage)), Permissions.AdminManage, ApplicationRoles.Admin));

    [Fact]
    public void Neither_is_refused() =>
        Assert.False(SeatOrPermission.Holds(Principal(Role("Viewer"), Permission(Permissions.ItemsRead)), Permissions.AdminManage, ApplicationRoles.Admin));

    [Fact]
    public void A_permission_string_on_the_seat_claim_is_not_a_permission() =>
        // The raw string stays on ClaimTypes.Role after the handler's inbound mapping. Only the
        // projected claim type counts, so an unprojected one grants nothing here.
        Assert.False(SeatOrPermission.Holds(Principal(Role(Permissions.AdminManage)), Permissions.AdminManage, ApplicationRoles.Admin));

    [Fact]
    public void A_permission_claim_value_is_not_a_seat() =>
        Assert.False(SeatOrPermission.Holds(Principal(Permission("admin")), Permissions.AdminManage, ApplicationRoles.Admin));

    private static Claim Role(string value) => new(ClaimTypes.Role, value);

    private static Claim Permission(string value) => new(PermissionClaims.ClaimType, value);

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "TestBearer"));
}
