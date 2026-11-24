using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Application.Portals;
using AjBoilerplate.Infrastructure.Portals;
using Microsoft.Extensions.Configuration;

namespace AjBoilerplate.UnitTests.Portals;

public sealed class PortalAccessTests
{
    private static readonly IReadOnlyList<PortalEntry> Catalogue =
    [
        new("home", "Home", "/", null),
        new("reports", "Reports", "/reports", "read"),
        new("studio", "Studio", "/studio", "write"),
        new("admin", "Admin", "/admin", "administer"),
        new("typo", "Typo", "/typo", "adminster"),
    ];

    private static IReadOnlyList<string> KeysFor(string? role)
    {
        var roles = role is null ? [] : new[] { role };
        var capabilities = UserCapabilitiesDto.From(RoleCapabilities.For(roles));
        var profile = new UserProfileDto("u", "U", "u@x", roles, capabilities, []);
        return new PortalAccessService(new FixedUser(profile), new FixedCatalogue(Catalogue)).ForCaller().Select(p => p.Key).ToList();
    }

    [Fact]
    public void Each_seat_sees_exactly_the_portals_its_capabilities_allow() =>
        Assert.Multiple(
            () => Assert.Equal(["home", "reports", "studio", "admin"], KeysFor(ApplicationRoles.Admin)),
            () => Assert.Equal(["home", "reports", "studio"], KeysFor(ApplicationRoles.Editor)),
            () => Assert.Equal(["home", "reports"], KeysFor(ApplicationRoles.Viewer)));

    [Fact]
    public void A_person_with_no_role_sees_nothing_and_a_misspelt_requirement_hides_the_portal_from_everybody()
    {
        Assert.Empty(KeysFor(null));
        Assert.DoesNotContain("typo", KeysFor(ApplicationRoles.Admin));
    }

    [Fact]
    public void The_catalogue_reads_the_Portals_section_skipping_incomplete_entries_and_duplicate_keys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portals:0:Key"] = "reports",
            ["Portals:0:Name"] = "Reports",
            ["Portals:0:Url"] = "/reports",
            ["Portals:0:RequiredCapability"] = " read ",
            ["Portals:1:Key"] = "nourl",
            ["Portals:1:Name"] = "No url",
            ["Portals:2:Key"] = "REPORTS",
            ["Portals:2:Name"] = "Again",
            ["Portals:2:Url"] = "/again",
            ["Portals:3:Key"] = "home",
            ["Portals:3:Name"] = "Home",
            ["Portals:3:Url"] = "/",
        }).Build();

        var all = new ConfigurationPortalCatalogue(configuration).All;

        Assert.Equal(["reports", "home"], all.Select(p => p.Key));
        Assert.Equal("read", all[0].RequiredCapability);
        Assert.Null(all[1].RequiredCapability);
    }

    [Fact]
    public void No_Portals_section_is_an_empty_list_not_an_error() =>
        Assert.Empty(new ConfigurationPortalCatalogue(new ConfigurationBuilder().Build()).All);

    private sealed class FixedUser(UserProfileDto profile) : ICurrentUserService
    {
        public UserProfileDto GetProfile() => profile;
    }

    private sealed class FixedCatalogue(IReadOnlyList<PortalEntry> all) : IPortalCatalogue
    {
        public IReadOnlyList<PortalEntry> All => all;
    }
}
