using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Identity;
using AjBoilerplate.Contracts.Items;
using AjBoilerplate.IntegrationTests.Support;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// End-to-end coverage of <c>GET /api/v1/me</c> — the endpoint the frontend's OIDC provider calls to
/// learn what the signed-in user may do (<c>libs/auth/src/lib/providers/oidc-provider.ts</c>).
///
/// The shape assertions here are deliberately made against the RAW JSON as well as the typed DTO.
/// The frontend consumes this through `envelopeInterceptor`, which unwraps `data` and hands the
/// result to code typed as `UserProfile`; if a property is renamed, re-cased, or the envelope is
/// dropped, a typed round trip through the server's own contract assembly would still pass while the
/// browser silently received `undefined`. Only the property names on the wire catch that.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MeApiTests
{
    private readonly ApiFactory _factory;

    public MeApiTests(SqlServerFixture fixture) => _factory = fixture.Api;

    [Fact]
    public async Task An_anonymous_caller_gets_an_enveloped_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/me", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.False(envelope.IsSuccess);
        Assert.Equal(EnvelopeCodes.Unauthorized, envelope.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));
    }

    [Fact]
    public async Task The_profile_describes_the_authenticated_caller()
    {
        var client = _factory.CreateClientAs(ApplicationRoles.Editor, "oid-me-editor");

        var profile = await GetProfileAsync(client);

        Assert.Equal("oid-me-editor", profile.UserId);
        Assert.Equal("Test User", profile.DisplayName);
        Assert.Equal([ApplicationRoles.Editor], profile.Roles);
    }

    [Theory]
    [InlineData(ApplicationRoles.Admin, true, true, true, true, true)]
    [InlineData(ApplicationRoles.Editor, true, true, true, true, false)]
    [InlineData(ApplicationRoles.Viewer, true, false, false, false, false)]
    public async Task The_capabilities_come_from_the_server_not_the_caller(
        string role, bool view, bool create, bool edit, bool delete, bool administer)
    {
        var client = _factory.CreateClientAs(role, $"oid-{role}");

        var capabilities = (await GetProfileAsync(client)).Capabilities;

        Assert.Equal(view, capabilities.CanView);
        Assert.Equal(create, capabilities.CanCreate);
        Assert.Equal(edit, capabilities.CanEdit);
        Assert.Equal(delete, capabilities.CanDelete);
        Assert.Equal(administer, capabilities.CanAdminister);
    }

    [Fact]
    public async Task A_caller_holding_an_unrecognised_role_is_answered_with_no_capabilities_not_a_403()
    {
        // The endpoint carries a bare [Authorize], not Policies.ReadAccess, precisely so this caller
        // gets an answer. Under ReadAccess this would be a 403 and the client could not distinguish
        // "your account has no permissions" from "the permissions endpoint is broken".
        var client = _factory.CreateClientAs("superuser", "oid-unprovisioned");

        var response = await client.GetAsync("/api/v1/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadEnvelopeAsync<UserProfileResponse>();
        Assert.Empty(profile.Roles);
        Assert.False(profile.Capabilities.CanView);
        Assert.False(profile.Capabilities.CanAdminister);
    }

    [Fact]
    public async Task A_viewer_is_told_it_cannot_write_and_the_server_agrees()
    {
        // The claim the capability flags make must match what the pipeline actually enforces,
        // through the real authorization policies rather than a re-derivation of them. A flag that
        // disagrees with the policy is the failure this whole endpoint exists to prevent.
        var client = _factory.CreateClientAs(ApplicationRoles.Viewer, "oid-me-viewer");

        Assert.False((await GetProfileAsync(client)).Capabilities.CanCreate);

        var write = await CreateItemAsync(client, "Blocked");
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task An_editor_is_told_it_may_create_and_the_server_agrees()
    {
        // The other half of the same guarantee: a flag must not be conservatively false either, or
        // the UI hides an action the user is entitled to and the permission model has forked.
        var client = _factory.CreateClientAs(ApplicationRoles.Editor, "oid-me-editor-write");

        Assert.True((await GetProfileAsync(client)).Capabilities.CanCreate);

        var write = await CreateItemAsync(client, $"Allowed {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
    }

    [Fact]
    public async Task The_wire_shape_is_exactly_what_the_frontend_parses()
    {
        // `oidc-provider.ts` types this call as `UserProfile` and relies on `envelopeInterceptor`
        // returning `body.data`. So the contract is: an ApiResponse envelope whose `data` carries
        // camelCase `userId`/`displayName`/`email`/`roles`/`capabilities`, with the five capability
        // flags named exactly as `Capabilities` in `libs/auth/src/lib/roles.ts` declares them.
        var client = _factory.CreateClientAs(ApplicationRoles.Admin, "oid-shape");

        var response = await client.GetAsync("/api/v1/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());

        var data = root.GetProperty("data");
        Assert.Equal("oid-shape", data.GetProperty("userId").GetString());
        Assert.Equal("Test User", data.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.String, data.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("roles").ValueKind);

        var capabilities = data.GetProperty("capabilities");
        foreach (var flag in new[] { "canView", "canCreate", "canEdit", "canDelete", "canAdminister" })
        {
            Assert.True(
                capabilities.TryGetProperty(flag, out var value),
                $"The frontend's Capabilities interface requires '{flag}'.");
            Assert.Equal(JsonValueKind.True, value.ValueKind);
        }

        // The client reads role names to display them. They are the canonical display names the
        // server's own vocabulary defines, never the authorization server's lowercase keys.
        Assert.Equal(
            [ApplicationRoles.Admin],
            data.GetProperty("roles").EnumerateArray().Select(r => r.GetString() ?? string.Empty).ToArray());
    }

    private static Task<HttpResponseMessage> CreateItemAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/api/v1/items", new CreateItemRequest(name, null, null), CancellationToken.None);

    private static async Task<UserProfileResponse> GetProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadEnvelopeAsync<UserProfileResponse>();
    }
}
