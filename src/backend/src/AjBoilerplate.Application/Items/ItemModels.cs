using AjBoilerplate.Application.Common;
using AjBoilerplate.Domain.Items;

namespace AjBoilerplate.Application.Items;

/// <summary>
/// SAMPLE SLICE. The Application layer's own read model for an item — mapped to
/// <c>AjBoilerplate.Contracts.Items.ItemResponse</c> by the Api layer, never returned raw.
///
/// <paramref name="Status"/> is the enum's canonical NAME, not the enum type. That is deliberate:
/// the Api layer must not reference the Domain assembly (the architecture tests enforce it), so
/// every Domain type stops at this boundary and the Api sees strings. <see cref="ItemStatusNames"/>
/// owns the string ↔ enum translation in both directions.
/// </summary>
public sealed record ItemDto(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    byte[] RowVersion);

/// <summary>Filter/paging arguments for the item list.</summary>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page, bounded by <see cref="Paging.MaxPageSize"/>.</param>
/// <param name="Search">Optional case-insensitive substring match on name and description.</param>
public sealed record ListItemsQuery(int Page, int PageSize, string? Search);

/// <summary>Create an item. <paramref name="Status"/> is optional; null or blank means
/// <see cref="ItemStatus.Draft"/>.</summary>
public sealed record CreateItemCommand(string Name, string? Description, string? Status);

/// <summary>Update an item, guarded by the concurrency token the caller last read.
/// <paramref name="Status"/> is required.</summary>
public sealed record UpdateItemCommand(Guid Id, string Name, string? Description, string Status, byte[] RowVersion);

/// <summary>
/// Translates between <see cref="ItemStatus"/> and its wire name. One place owns it, so the
/// validator's "is this a known status?" rule and the service's parse can never disagree — the
/// classic version of that bug being a validator that accepts a value the parser then silently maps
/// to the enum's default.
/// </summary>
public static class ItemStatusNames
{
    /// <summary>Applied when a create request omits the status.</summary>
    public static readonly ItemStatus Default = ItemStatus.Draft;

    /// <summary>Every accepted value, for validation messages and API documentation.</summary>
    public static IReadOnlyList<string> All { get; } = Enum.GetNames<ItemStatus>();

    /// <summary>True when <paramref name="status"/> names a known member (case-insensitive).
    /// A numeric string is rejected: "1" is not a status a client should ever send.</summary>
    public static bool IsKnown(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && Enum.TryParse<ItemStatus>(status, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
        && !char.IsDigit(status.Trim()[0]);

    /// <summary>Parses a value already known to be valid (its validator ran first). Blank yields
    /// <see cref="Default"/>.</summary>
    public static ItemStatus Parse(string? status) =>
        string.IsNullOrWhiteSpace(status) ? Default : Enum.Parse<ItemStatus>(status, ignoreCase: true);
}

/// <summary>
/// The caller's copy of the item was superseded by a save from elsewhere. Surfaces as an enveloped
/// 409 so the client can re-read and retry — never as a silent last-write-wins overwrite.
/// </summary>
public sealed class StaleItemException : ConflictException
{
    public StaleItemException(Guid id)
        : base($"Item {id} was modified by someone else. Reload it and try again.")
    {
    }
}
