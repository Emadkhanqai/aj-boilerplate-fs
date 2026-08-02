using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Items;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// SAMPLE SLICE. The one controller that ships, proving the whole request path: versioned route,
/// DTO-only binding, policy-based authorization, Application-layer delegation, and the
/// <c>ApiResponse</c> envelope (applied globally by <c>EnvelopeResultFilter</c> — note that no
/// action builds one).
///
/// Delete it with the rest of the <c>Item</c> sample, or copy its shape for your first real
/// resource. Reads need <see cref="Policies.ReadAccess"/>; every mutation additionally needs
/// <see cref="Policies.WriteAccess"/>, so a viewer can list and fetch but cannot change anything.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.ReadAccess)]
[Route("api/v1/items")]
[Produces("application/json")]
public sealed class ItemsController : ControllerBase
{
    private readonly IItemService _items;

    public ItemsController(IItemService items) => _items = items;

    /// <summary>List items, newest first, optionally filtered by a case-insensitive substring of
    /// name or description.</summary>
    /// <param name="page">1-based page number. Defaults to 1.</param>
    /// <param name="pageSize">Rows per page, 1–100. Defaults to 20.</param>
    /// <param name="search">Optional substring filter.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<ItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] int page = Paging.DefaultPage,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var result = await _items.ListAsync(new ListItemsQuery(page, pageSize, search), ct);
        return Ok(new PagedResponse<ItemResponse>(
            result.Items.Select(ToResponse).ToList(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    /// <summary>Get a single item by id.</summary>
    [HttpGet("{id:guid}", Name = nameof(GetById))]
    [ProducesResponseType(typeof(ItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var item = await _items.GetAsync(id, ct);
        return item is null ? NotFound() : Ok(ToResponse(item));
    }

    /// <summary>Create an item. Returns 201 with a <c>Location</c> header pointing at the new
    /// resource.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.WriteAccess)]
    [ProducesResponseType(typeof(ItemResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateItemRequest request, CancellationToken ct)
    {
        var command = new CreateItemCommand(request.Name, request.Description, request.Status);
        var created = await _items.CreateAsync(command, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(created));
    }

    /// <summary>
    /// Replace an item's name, description, and status. <c>rowVersion</c> must be the token from the
    /// caller's last read; a stale one is rejected with 409 rather than silently overwriting whatever
    /// someone else saved in the meantime.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.WriteAccess)]
    [ProducesResponseType(typeof(ItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, UpdateItemRequest request, CancellationToken ct)
    {
        var command = new UpdateItemCommand(
            id,
            request.Name,
            request.Description,
            request.Status,
            ParseRowVersion(request.RowVersion));

        var updated = await _items.UpdateAsync(command, ct);
        return updated is null ? NotFound() : Ok(ToResponse(updated));
    }

    /// <summary>Delete an item. 204 on success, 404 when no item has that id.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.WriteAccess)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _items.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    private static ItemResponse ToResponse(ItemDto item) => new(
        item.Id,
        item.Name,
        item.Description,
        item.Status,
        item.CreatedAt,
        item.UpdatedAt,
        Convert.ToBase64String(item.RowVersion));

    /// <summary>
    /// Decodes the base64 concurrency token. A malformed value yields an empty array rather than an
    /// exception, so the command validator turns it into a clean 400 — never a 500 for garbage input,
    /// and never a 409, which would wrongly tell the client that someone else had edited the record.
    /// </summary>
    private static byte[] ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            return [];
        }

        return Convert.TryFromBase64String(rowVersion, new byte[rowVersion.Length], out var written)
            ? Convert.FromBase64String(rowVersion)[..written]
            : [];
    }
}
