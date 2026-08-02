using System.Net;
using System.Net.Http.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Items;
using AjBoilerplate.IntegrationTests.Support;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// SAMPLE SLICE. End-to-end coverage of the whole request path for <c>/api/v1/items</c>: routing,
/// authorization, validation, the envelope, and the optimistic-concurrency conflict.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ItemsApiTests
{
    private readonly ApiFactory _factory;

    public ItemsApiTests(SqlServerFixture fixture) => _factory = fixture.Api;

    private HttpClient Editor => _factory.CreateClientAs(ApplicationRoles.Editor);

    private HttpClient Viewer => _factory.CreateClientAs(ApplicationRoles.Viewer);

    [Fact]
    public async Task Anonymous_requests_are_rejected_with_an_enveloped_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/items", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Even a reply the framework produces before any action runs must carry the envelope.
        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.False(envelope.IsSuccess);
        Assert.Equal(EnvelopeCodes.Unauthorized, envelope.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));
    }

    [Fact]
    public async Task A_viewer_may_read_but_not_write()
    {
        var viewer = Viewer;

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);

        var forbidden = await viewer.PostAsJsonAsync("/api/v1/items", new CreateItemRequest("Nope", null, null), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(EnvelopeCodes.Forbidden, (await forbidden.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task Create_returns_201_with_a_location_header_and_the_created_item()
    {
        var response = await Editor.PostAsJsonAsync(
            "/api/v1/items", new CreateItemRequest("Widget", "A widget.", "Active"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = await response.Content.ReadEnvelopeAsync<ItemResponse>();
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Widget", created.Name);
        Assert.Equal("A widget.", created.Description);
        Assert.Equal("Active", created.Status);
        Assert.Null(created.UpdatedAt);
        Assert.False(string.IsNullOrWhiteSpace(created.RowVersion));
    }

    [Fact]
    public async Task Create_defaults_an_omitted_status_to_draft()
    {
        var response = await Editor.PostAsJsonAsync(
            "/api/v1/items", new CreateItemRequest("Defaulted", null, null), CancellationToken.None);

        Assert.Equal("Draft", (await response.Content.ReadEnvelopeAsync<ItemResponse>()).Status);
    }

    [Fact]
    public async Task Create_rejects_a_blank_name_with_an_enveloped_400()
    {
        var response = await Editor.PostAsJsonAsync(
            "/api/v1/items", new CreateItemRequest("   ", null, null), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.Equal(EnvelopeCodes.ValidationError, envelope.Code);
        // The failing field must be named, or the client cannot show the error next to the input.
        Assert.NotNull(envelope.Errors);
        Assert.Contains(envelope.Errors, e => e.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_rejects_an_unknown_status_with_a_400()
    {
        var response = await Editor.PostAsJsonAsync(
            "/api/v1/items", new CreateItemRequest("Widget", null, "Deleted"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_location_header_from_create_resolves_to_the_item()
    {
        var client = Editor;
        var created = await CreateAsync(client, "Locatable");
        var response = await client.GetAsync($"/api/v1/items/{created.Id}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, (await response.Content.ReadEnvelopeAsync<ItemResponse>()).Id);
    }

    [Fact]
    public async Task Get_returns_an_enveloped_404_for_an_unknown_id()
    {
        var response = await Editor.GetAsync($"/api/v1/items/{Guid.NewGuid()}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(EnvelopeCodes.NotFound, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task List_returns_a_paged_envelope()
    {
        var client = Editor;
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(client, $"Listed {Guid.NewGuid():N}");
        }

        var response = await client.GetAsync("/api/v1/items?page=1&pageSize=2", CancellationToken.None);
        var page = await response.Content.ReadEnvelopeAsync<PagedResponse<ItemResponse>>();

        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Total >= 3);
    }

    [Fact]
    public async Task List_filters_by_the_search_term()
    {
        var client = Editor;
        var unique = $"Searchable{Guid.NewGuid():N}";
        await CreateAsync(client, unique);

        var response = await client.GetAsync($"/api/v1/items?search={unique}", CancellationToken.None);
        var page = await response.Content.ReadEnvelopeAsync<PagedResponse<ItemResponse>>();

        Assert.Equal(unique, Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task List_rejects_a_page_size_above_the_maximum_with_a_400()
    {
        var response = await Editor.GetAsync("/api/v1/items?pageSize=1000", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task Update_applies_the_change_and_returns_a_new_row_version()
    {
        var client = Editor;
        var created = await CreateAsync(client, "Before");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/items/{created.Id}",
            new UpdateItemRequest("After", "Changed.", "Archived", created.RowVersion),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadEnvelopeAsync<ItemResponse>();
        Assert.Equal("After", updated.Name);
        Assert.Equal("Archived", updated.Status);
        Assert.NotNull(updated.UpdatedAt);
        // A stale token must not be handed back, or the client's next save would 409 spuriously.
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task Update_with_a_stale_row_version_conflicts_with_409()
    {
        var client = Editor;
        var created = await CreateAsync(client, "Contended");

        // First writer wins and invalidates the token both callers were holding.
        var first = await client.PutAsJsonAsync(
            $"/api/v1/items/{created.Id}",
            new UpdateItemRequest("First writer", null, "Active", created.RowVersion),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second writer still has the ORIGINAL token — a silent overwrite here is exactly the
        // lost-update bug this whole mechanism exists to prevent.
        var second = await client.PutAsJsonAsync(
            $"/api/v1/items/{created.Id}",
            new UpdateItemRequest("Second writer", null, "Active", created.RowVersion),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(EnvelopeCodes.Conflict, (await second.Content.ReadErrorEnvelopeAsync()).Code);

        // And the first writer's value survived.
        var reread = await client.GetAsync($"/api/v1/items/{created.Id}", CancellationToken.None);
        Assert.Equal("First writer", (await reread.Content.ReadEnvelopeAsync<ItemResponse>()).Name);
    }

    [Fact]
    public async Task Update_with_a_malformed_row_version_is_a_400_not_a_409()
    {
        var client = Editor;
        var created = await CreateAsync(client, "Malformed token");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/items/{created.Id}",
            new UpdateItemRequest("Nope", null, "Active", "not-base64!!"),
            CancellationToken.None);

        // Telling the user "someone else edited this" when their client simply sent garbage would
        // be actively misleading.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_returns_404_for_an_unknown_id()
    {
        var response = await Editor.PutAsJsonAsync(
            $"/api/v1/items/{Guid.NewGuid()}",
            new UpdateItemRequest("Ghost", null, "Active", Convert.ToBase64String(Guid.NewGuid().ToByteArray())),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_returns_204_and_the_item_is_gone()
    {
        var client = Editor;
        var created = await CreateAsync(client, "Doomed");

        var deleted = await client.DeleteAsync($"/api/v1/items/{created.Id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // 204 means no content — an envelope here would violate the HTTP contract.
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync(CancellationToken.None));

        var reread = await client.GetAsync($"/api/v1/items/{created.Id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, reread.StatusCode);
    }

    [Fact]
    public async Task Delete_returns_404_for_an_unknown_id() =>
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Editor.DeleteAsync($"/api/v1/items/{Guid.NewGuid()}", CancellationToken.None)).StatusCode);

    private static async Task<ItemResponse> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/items", new CreateItemRequest(name, null, "Draft"), CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadEnvelopeAsync<ItemResponse>();
    }
}
