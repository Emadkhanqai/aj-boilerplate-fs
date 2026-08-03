using System.Security.Claims;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Identity;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>
/// How roles are read off an authenticated principal. This is the highest-consequence silent-failure
/// surface in the identity code: every source here fails CLOSED, so getting it wrong produces no
/// exception and no log — just a caller who resolves to no role.
///
/// <para>
/// The tests below deliberately construct <see cref="ClaimTypes.Role"/> claims, not claims literally
/// named <c>roles</c>. That distinction is the whole point: JwtBearer's default inbound claim-type
/// mapping renames a token's flat top-level <c>roles</c> claim to <see cref="ClaimTypes.Role"/>
/// before any of this code runs, so a test that hand-builds a <c>roles</c>-typed claim exercises a
/// shape the framework never actually delivers — and will happily stay green while the real thing is
/// broken.
/// </para>
/// </summary>
public sealed class KeycloakRoleClaimsTransformationTests
{
    private const string ConfiguredClientId = "my-api";

    [Fact]
    public void A_role_claim_produced_by_the_handlers_own_inbound_mapping_resolves_to_the_canonical_role()
    {
        // The exact production shape: a realm emits {"roles":["editor"]}, JwtBearer renames it, and
        // this is all that is left on the principal. Nothing named "roles" survives.
        var roles = ExtractFrom(new Claim(ClaimTypes.Role, "editor"));

        Assert.Equal(ApplicationRoles.Editor, Assert.Single(roles));
    }

    [Fact]
    public void The_nested_shapes_and_the_mapped_role_claim_merge_without_duplicating()
    {
        // A token may carry more than one shape at once, and the mapped claim overlaps one of them.
        // Every source must contribute, and the overlap must collapse to a single canonical entry.
        var roles = ExtractFrom(
            new Claim("realm_access", """{"roles":["viewer"]}"""),
            new Claim("resource_access", $$$"""{"{{{ConfiguredClientId}}}":{"roles":["editor"]}}"""),
            new Claim(ClaimTypes.Role, "editor"),
            new Claim(ClaimTypes.Role, "admin"));

        Assert.Equal(
            new[] { ApplicationRoles.Viewer, ApplicationRoles.Editor, ApplicationRoles.Admin },
            roles);
    }

    [Fact]
    public void An_unmapped_key_on_the_role_claim_still_grants_nothing() =>
        // Reading a new claim type must not weaken the fail-closed rule: the value still goes through
        // the one canonicalisation table, and anything unrecognised is dropped.
        Assert.Empty(ExtractFrom(new Claim(ClaimTypes.Role, "superuser")));

    [Fact]
    public async Task Transforming_twice_adds_the_canonical_role_once_and_never_grows_the_principal()
    {
        // ClaimTypes.Role is now both an input to the extraction and the type this writes to, and
        // TransformAsync runs on every request — more than once per request when something calls
        // AuthenticateAsync again. If canonicalisation, Distinct or the IsInRole filter failed to
        // hold, the principal would gain a claim per pass and grow without bound.
        var principal = Principal(new Claim(ClaimTypes.Role, "editor"));
        var transformation = Transformation();

        await transformation.TransformAsync(principal);
        var afterFirstPass = RoleClaimsOf(principal);

        await transformation.TransformAsync(principal);
        var afterSecondPass = RoleClaimsOf(principal);

        // The raw key the handler put there, plus the canonical name this appended — once.
        Assert.Equal(new[] { "editor", ApplicationRoles.Editor }, afterFirstPass);
        Assert.Equal(afterFirstPass, afterSecondPass);
    }

    [Fact]
    public async Task A_token_that_already_uses_the_display_name_gains_no_second_claim() =>
        // Canonicalising a display name returns it unchanged, so IsInRole already matches and there
        // is nothing to add. Without that, one seat would end up claimed twice.
        Assert.Equal(
            new[] { ApplicationRoles.Admin },
            await TransformedRoleClaimsOf(Principal(new Claim(ClaimTypes.Role, ApplicationRoles.Admin))));

    [Fact]
    public async Task An_unauthenticated_principal_is_left_untouched() =>
        // Anonymous requests reach the transformation too. It must add nothing — the raw key stays
        // exactly as it was, with no canonical name appended alongside it.
        Assert.Equal(
            new[] { "admin" },
            await TransformedRoleClaimsOf(new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "admin") }))));

    private static IReadOnlyList<string> ExtractFrom(params Claim[] claims) =>
        KeycloakRoleClaimsTransformation.ExtractApplicationRoles(Principal(claims), ConfiguredClientId);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        // The authentication type is what makes the identity authenticated; without it
        // TransformAsync correctly declines to touch the principal at all.
        new(new ClaimsIdentity(claims, "TestBearer"));

    private static KeycloakRoleClaimsTransformation Transformation() =>
        new(Options.Create(new KeycloakOptions { ClientId = ConfiguredClientId }));

    private static async Task<List<string>> TransformedRoleClaimsOf(ClaimsPrincipal principal) =>
        RoleClaimsOf(await Transformation().TransformAsync(principal));

    private static List<string> RoleClaimsOf(ClaimsPrincipal principal) =>
        principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToList();
}
