using System.Net;
using System.Text.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Identity;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AjBoilerplate.IntegrationTests.Api;

[Collection(DatabaseCollection.Name)]
public sealed class MePortalsApiTests : IDisposable
{
    private const string Url = "/api/v1/me/portals";

    private readonly WebApplicationFactory<Program> _factory;

    public MePortalsApiTests(SqlServerFixture fixture) =>
        _factory = fixture.Api.WithWebHostBuilder(b => b
            .UseSetting("Portals:0:Key", "reports").UseSetting("Portals:0:Name", "Reports").UseSetting("Portals:0:Url", "/reports")
            .UseSetting("Portals:1:Key", "studio").UseSetting("Portals:1:Name", "Studio").UseSetting("Portals:1:Url", "https://studio.example")
            .UseSetting("Portals:1:RequiredCapability", "write")
            .UseSetting("Portals:2:Key", "admin").UseSetting("Portals:2:Name", "Admin").UseSetting("Portals:2:Url", "/admin")
            .UseSetting("Portals:2:RequiredCapability", "administer"));

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(ApplicationRoles.Viewer, "reports")]
    [InlineData(ApplicationRoles.Editor, "reports,studio")]
    [InlineData(ApplicationRoles.Admin, "reports,studio,admin")]
    public async Task Each_seat_is_offered_the_portals_its_capabilities_allow(string role, string expectedKeys)
    {
        var response = await _factory.CreateClientAsRole(role).GetAsync(Url, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadEnvelopeAsync<MePortalsResponse>();
        Assert.Equal(expectedKeys.Split(','), data.PortalKeys);
        Assert.Equal(expectedKeys.Split(','), data.Portals.Select(p => p.Key));
    }

    [Fact]
    public async Task The_wire_has_the_field_names_a_menu_binds_to()
    {
        var response = await _factory.CreateClientAsRole(ApplicationRoles.Editor).GetAsync(Url, CancellationToken.None);

        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var data = raw.RootElement.GetProperty("data");
        Assert.Equal(new[] { "portalKeys", "portals" }, data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "key", "name", "url" }, data.GetProperty("portals")[1].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("https://studio.example", data.GetProperty("portals")[1].GetProperty("url").GetString());
    }

    [Fact]
    public async Task An_authenticated_person_with_no_role_gets_an_honest_empty_list_not_a_403()
    {
        var client = _factory.CreateClientAsRole("superuser", "oid-no-role");

        var response = await client.GetAsync(Url, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadEnvelopeAsync<MePortalsResponse>()).PortalKeys);
    }

    [Fact]
    public async Task An_anonymous_caller_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url, CancellationToken.None)).StatusCode);
}
