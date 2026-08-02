using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>
/// The role vocabulary and the capabilities it grants. These matter more than they look: every
/// authorization policy resolves through <see cref="RoleCapabilities"/>, so a regression here is a
/// silent authorization change, not a visible failure.
/// </summary>
public sealed class RoleTests
{
    [Theory]
    [InlineData("admin", ApplicationRoles.Admin)]
    [InlineData("Admin", ApplicationRoles.Admin)]
    [InlineData("  EDITOR ", ApplicationRoles.Editor)]
    [InlineData("viewer", ApplicationRoles.Viewer)]
    public void Canonical_accepts_the_authorization_server_key_and_the_display_name(string input, string expected) =>
        // A real token carries BOTH spellings for one seat; both must land on the same value.
        Assert.Equal(expected, ApplicationRoles.Canonical(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("superuser")]
    public void Canonical_drops_an_unrecognised_role(string? input) =>
        // Fails closed: an unknown key never grants anything.
        Assert.Null(ApplicationRoles.Canonical(input));

    [Fact]
    public void Effective_picks_the_highest_authority_seat() =>
        Assert.Equal(ApplicationRoles.Admin, ApplicationRoles.Effective(["viewer", "admin", "editor"]));

    [Fact]
    public void Effective_is_order_independent() =>
        // Otherwise the answer would depend on claim enumeration order, which is not guaranteed.
        Assert.Equal(
            ApplicationRoles.Effective(["admin", "viewer"]),
            ApplicationRoles.Effective(["viewer", "admin"]));

    [Fact]
    public void Effective_returns_null_when_nothing_is_recognised() =>
        Assert.Null(ApplicationRoles.Effective(["superuser", "root"]));

    [Fact]
    public void Admin_can_do_everything()
    {
        var capabilities = RoleCapabilities.For([ApplicationRoles.Admin]);

        Assert.True(capabilities.CanRead);
        Assert.True(capabilities.CanWrite);
        Assert.True(capabilities.CanAdminister);
    }

    [Fact]
    public void Editor_can_write_but_not_administer()
    {
        var capabilities = RoleCapabilities.For(["editor"]);

        Assert.True(capabilities.CanRead);
        Assert.True(capabilities.CanWrite);
        Assert.False(capabilities.CanAdminister);
    }

    [Fact]
    public void Viewer_can_only_read()
    {
        var capabilities = RoleCapabilities.For(["viewer"]);

        Assert.True(capabilities.CanRead);
        Assert.False(capabilities.CanWrite);
        Assert.False(capabilities.CanAdminister);
    }

    [Fact]
    public void An_unrecognised_role_grants_nothing()
    {
        var capabilities = RoleCapabilities.For(["superuser"]);

        Assert.False(capabilities.CanRead);
        Assert.False(capabilities.CanWrite);
        Assert.False(capabilities.CanAdminister);
    }

    [Fact]
    public void No_roles_at_all_grants_nothing() =>
        Assert.False(RoleCapabilities.For([]).CanRead);

    [Fact]
    public void The_api_layer_translation_uses_the_same_table_as_the_application_layer() =>
        // Two copies of this table would be free to drift, and drift here fails silently: the
        // policies keep working while every list the user sees comes back empty.
        Assert.Equal(ApplicationRoles.Canonical("editor"), KeycloakRoles.ToApplicationRole("editor"));
}
