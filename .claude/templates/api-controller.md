# Template: API Controller (`AjBoilerplate.Api`)

Thin. Validate the shape, dispatch to Application, map to a Contracts DTO, return. **No
business logic.**

```csharp
namespace AjBoilerplate.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/items")]
[Produces("application/json")]
[Authorize(Policy = Policies.ItemsRead)]        // deny by default; opt in deliberately
public sealed class ItemsController : ControllerBase
{
    private readonly ISender _sender;

    public ItemsController(ISender sender) => _sender = sender;

    /// <summary>Returns a page of items.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResponse<ItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListItemsRequest request, CancellationToken ct)
    {
        var page = await _sender.Send(
            new ListItemsQuery(request.Page, request.PageSize, request.Search), ct);

        return Ok(ApiResponse<PagedResponse<ItemResponse>>.Success(page));
    }

    /// <summary>Returns a single item.</summary>
    /// <response code="404">No such item, or the caller may not know it exists.</response>
    [HttpGet("{id:int}", Name = nameof(GetById))]
    [ProducesResponseType(typeof(ApiResponse<ItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _sender.Send(new GetItemByIdQuery(id), ct);
        return Ok(ApiResponse<ItemResponse>.Success(item));
    }

    /// <summary>Creates an item.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ItemsWrite)]
    [ProducesResponseType(typeof(ApiResponse<ItemResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateItemRequest request, CancellationToken ct)
    {
        var created = await _sender.Send(
            new CreateItemCommand(request.Name, request.Description), ct);

        return CreatedAtAction(
            nameof(GetById),
            new { id = created.Id, version = "1.0" },
            ApiResponse<ItemResponse>.Success(created, statusCode: StatusCodes.Status201Created));
    }

    /// <summary>Updates an item. Requires <c>If-Match</c>.</summary>
    /// <response code="409">The item changed since it was read — reload and retry.</response>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.ItemsWrite)]
    [ProducesResponseType(typeof(ApiResponse<ItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        int id,
        UpdateItemRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken ct)
    {
        var updated = await _sender.Send(
            new UpdateItemCommand(id, request.Name, request.Description, ifMatch), ct);

        return Ok(ApiResponse<ItemResponse>.Success(updated));
    }

    /// <summary>Deletes an item.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.ItemsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _sender.Send(new DeleteItemCommand(id), ct);
        return NoContent();
    }
}
```

## Rules

- **No business logic here.** If there is an `if` about a domain rule, it belongs in
  Domain/Application.
- **DTOs come from `AjBoilerplate.Contracts`.** Never accept or return an EF Core entity —
  that is how mass assignment happens.
- **A policy on every action**, deny by default. Object-ownership checks happen in the handler
  *after* loading the resource, never from the route id alone.
- **Never catch and translate exceptions here.** The `IExceptionHandler` chain owns that, so
  every endpoint answers identically
  ([`../standards/error-handling.md`](../standards/error-handling.md)).
- **Document every status the action can return**, including the error shapes and their `code`
  values, so the generated client and the frontend both know what to expect.
- `CancellationToken` on every action; `201` carries a `Location` header; list endpoints are
  always paginated with a server-side cap.

See [`../standards/api-design.md`](../standards/api-design.md) and
[`../standards/api-response-format.md`](../standards/api-response-format.md).
