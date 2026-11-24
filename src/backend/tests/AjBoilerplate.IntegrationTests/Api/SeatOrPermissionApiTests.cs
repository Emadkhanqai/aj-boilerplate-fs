using System.Net;
using System.Net.Http.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Identity;
using AjBoilerplate.Contracts.Items;
using AjBoilerplate.IntegrationTests.Support;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// Authorization end to end through the real pipeline: a seat OR a permission admits, and a caller
/// holding neither is refused. The permission-only callers are the ones the union exists for — they
/// have no seat, so an implementation that quietly required one would pass every other test.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SeatOrPermissionApiTests
{
    private readonly ApiFactory _factory;

    public SeatOrPermissionApiTests(SqlServerFixture fixture) => _factory = fixture.Api;

    [Fact]
    public async Task Without_a_token_a_protected_endpoint_is_an_enveloped_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/items", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(EnvelopeCodes.Unauthorized, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task An_authenticated_caller_with_no_seat_and_no_permission_is_refused_with_a_403()
    {
        var client = _factory.CreateClientAs("superuser", "oid-no-capability");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task A_seat_is_admitted()
    {
        var client = _factory.CreateClientAs(ApplicationRoles.Viewer, "oid-seat-only");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task A_permission_with_no_seat_is_admitted_to_exactly_what_it_names()
    {
        var reader = _factory.CreateClientWithPermissions("oid-perm-read", Permissions.ItemsRead);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateItemAsync(reader, "Blocked")).StatusCode);
    }

    [Fact]
    public async Task A_write_permission_does_not_imply_read_so_it_is_not_enough_alone_to_write_an_item()
    {
        // ItemsController carries ReadAccess on the class and WriteAccess on its write actions, and
        // ASP.NET combines them with AND. A seat hides that, because Editor and Admin hold read too.
        // A permission does not: items:write names write and nothing else, so on its own it is
        // refused at the controller's read gate. Grant items:read beside it (next test).
        var writer = _factory.CreateClientWithPermissions("oid-perm-write-only", Permissions.ItemsWrite);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateItemAsync(writer, "Write-only")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Read_and_write_permissions_together_with_no_seat_may_write()
    {
        var writer = _factory.CreateClientWithPermissions("oid-perm-read-write", Permissions.ItemsRead, Permissions.ItemsWrite);

        Assert.Equal(HttpStatusCode.Created, (await CreateItemAsync(writer, $"By permission {Guid.NewGuid():N}")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_permission_grants_nothing()
    {
        var client = _factory.CreateClientWithPermissions("oid-perm-unknown", "items:delete", "ITEMS:READ-ALL");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task The_profile_reports_permissions_beside_the_flags_and_not_as_roles()
    {
        var client = _factory.CreateClientWithPermissions("oid-perm-me", Permissions.ItemsWrite, Permissions.AdminManage);

        var response = await client.GetAsync("/api/v1/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadEnvelopeAsync<UserProfileResponse>();

        Assert.Empty(profile.Roles);
        Assert.Equal([Permissions.ItemsWrite, Permissions.AdminManage], profile.Permissions);
        Assert.True(profile.Capabilities.CanCreate);
        Assert.True(profile.Capabilities.CanAdminister);
        Assert.False(profile.Capabilities.CanView);
    }

    private static Task<HttpResponseMessage> CreateItemAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/api/v1/items", new CreateItemRequest(name, null, null), CancellationToken.None);
}
