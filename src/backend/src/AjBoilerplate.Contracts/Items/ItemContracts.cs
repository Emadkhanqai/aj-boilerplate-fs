namespace AjBoilerplate.Contracts.Items;

/// <summary>
/// SAMPLE SLICE DTOs — delete or rename alongside the rest of the <c>Item</c> sample.
///
/// These are the only shapes that cross the API boundary for items; the EF entity is never bound to
/// a request or serialized into a response.
/// </summary>
/// <param name="Id">Server-assigned identifier.</param>
/// <param name="Name">Display name, 1–200 characters.</param>
/// <param name="Description">Optional free text, up to 2000 characters.</param>
/// <param name="Status">One of <c>Draft</c>, <c>Active</c>, <c>Archived</c>.</param>
/// <param name="CreatedAt">When the item was created, UTC.</param>
/// <param name="UpdatedAt">When the item was last modified, UTC; null if never modified.</param>
/// <param name="RowVersion">Base64 optimistic-concurrency token. Send it back verbatim on update;
/// a stale value is rejected with 409.</param>
public sealed record ItemResponse(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string RowVersion);

/// <summary>Create-item request body.</summary>
/// <param name="Name">Required, 1–200 characters.</param>
/// <param name="Description">Optional, up to 2000 characters.</param>
/// <param name="Status">Optional; defaults to <c>Draft</c> when omitted.</param>
public sealed record CreateItemRequest(string Name, string? Description, string? Status);

/// <summary>Update-item request body.</summary>
/// <param name="Name">Required, 1–200 characters.</param>
/// <param name="Description">Optional, up to 2000 characters.</param>
/// <param name="Status">Required; one of <c>Draft</c>, <c>Active</c>, <c>Archived</c>.</param>
/// <param name="RowVersion">Required. The base64 token from the last read of this item.</param>
public sealed record UpdateItemRequest(string Name, string? Description, string Status, string RowVersion);
