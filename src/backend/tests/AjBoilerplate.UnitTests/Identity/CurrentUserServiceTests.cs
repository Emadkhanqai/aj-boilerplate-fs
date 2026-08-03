using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>
/// The projection behind <c>GET /api/v1/me</c>. What matters here is not that fields are copied but
/// that the capability flags a client gates its UI on are DERIVED from the same
/// <see cref="RoleCapabilities"/> table the authorization policies enforce — a second, independent
/// table would drift silently, and the drift only shows up as a user who cannot press a button the
/// server would have honoured.
/// </summary>
public sealed class CurrentUserServiceTests
{
    [Fact]
    public void The_profile_reports_the_authenticated_subject()
    {
        var profile = ProfileFor(ApplicationRoles.Editor);

        Assert.Equal("oid-42", profile.UserId);
        Assert.Equal("Test User", profile.DisplayName);
        Assert.Equal("test.user@example.com", profile.Email);
    }

    [Fact]
    public void The_profile_reports_every_canonical_role_the_caller_holds() =>
        Assert.Equal(
            [ApplicationRoles.Editor, ApplicationRoles.Viewer],
            ProfileFor(ApplicationRoles.Editor, ApplicationRoles.Viewer).Roles);

    [Theory]
    [InlineData(ApplicationRoles.Admin, true, true, true, true, true)]
    [InlineData(ApplicationRoles.Editor, true, true, true, true, false)]
    [InlineData(ApplicationRoles.Viewer, true, false, false, false, false)]
    public void The_capability_flags_match_what_the_server_would_enforce(
        string role, bool view, bool create, bool edit, bool delete, bool administer)
    {
        var capabilities = ProfileFor(role).Capabilities;

        Assert.Equal(view, capabilities.CanView);
        Assert.Equal(create, capabilities.CanCreate);
        Assert.Equal(edit, capabilities.CanEdit);
        Assert.Equal(delete, capabilities.CanDelete);
        Assert.Equal(administer, capabilities.CanAdminister);
    }

    [Fact]
    public void An_editor_is_told_it_may_delete_because_the_server_would_let_it()
    {
        // ItemsController's DELETE carries Policies.WriteAccess, which an Editor satisfies. Reporting
        // canDelete: false here would be a lie about the server's behaviour — the exact drift this
        // projection exists to prevent — so it is asserted on its own rather than only inside the
        // table above, where a future edit could quietly flip it.
        Assert.True(RoleCapabilities.For([ApplicationRoles.Editor]).CanWrite);
        Assert.True(ProfileFor(ApplicationRoles.Editor).Capabilities.CanDelete);
    }

    [Fact]
    public void An_unrecognised_role_is_dropped_rather_than_reported_to_the_client() =>
        // A role with no capability table behind it grants nothing, so naming it in the profile would
        // describe authority that does not exist — and would hand a client a role string its own
        // vocabulary cannot match.
        Assert.Empty(ProfileFor("superuser").Roles);

    [Fact]
    public void The_reported_roles_are_canonical_even_if_the_claims_source_hands_over_raw_keys() =>
        // IActorClaims promises canonical names and HttpContextActorClaims keeps that promise, but
        // this endpoint is what a client is told it IS. The guarantee is made true here rather than
        // assumed, so a second IActorClaims implementation cannot leak "editor" onto the wire.
        Assert.Equal([ApplicationRoles.Editor], ProfileFor("editor").Roles);

    [Fact]
    public void An_authenticated_caller_holding_no_recognised_role_gets_a_profile_with_nothing_granted()
    {
        // NOT a 403. This caller is exactly who /me exists for: signed in, not provisioned. An error
        // would leave the client unable to tell "you have no permissions" from "the endpoint broke".
        var profile = ProfileFor("superuser");

        Assert.Empty(profile.Roles);
        Assert.False(profile.Capabilities.CanView);
        Assert.False(profile.Capabilities.CanCreate);
        Assert.False(profile.Capabilities.CanEdit);
        Assert.False(profile.Capabilities.CanDelete);
        Assert.False(profile.Capabilities.CanAdminister);
    }

    [Theory]
    [InlineData(ActorIdentifiers.Anonymous)]
    [InlineData(ActorIdentifiers.System)]
    [InlineData("")]
    [InlineData("   ")]
    public void There_is_no_profile_for_a_caller_who_is_not_a_real_person(string actorId)
    {
        // Defence in depth behind the endpoint's [Authorize]. A "who am I" answer for the anonymous
        // pseudo-identity would look entirely plausible and describe nobody.
        var service = new CurrentUserService(new StubActorClaims(actorId, [ApplicationRoles.Admin]));

        Assert.Throws<ForbiddenException>(() => service.GetProfile());
    }

    private static UserProfileDto ProfileFor(params string[] roles) =>
        new CurrentUserService(new StubActorClaims("oid-42", roles)).GetProfile();

    /// <summary>
    /// The Api layer's <c>HttpContextActorClaims</c> reduced to what this service actually reads.
    /// Deliberately hands over whatever role strings the test supplies — including raw keys and
    /// unrecognised ones — rather than pre-canonicalising them, so the service's own defensive
    /// canonicalisation is what the assertions above are actually testing.
    /// </summary>
    private sealed class StubActorClaims : IActorClaims
    {
        public StubActorClaims(string id, IReadOnlyList<string> roles)
        {
            Id = id;
            Roles = roles;
        }

        public string Id { get; }

        public string Name => "Test User";

        public string Role => ApplicationRoles.Effective(Roles) ?? string.Empty;

        public string Email => "test.user@example.com";

        public IReadOnlyList<string> Roles { get; }

        public IReadOnlyList<string> Groups => [];
    }
}
