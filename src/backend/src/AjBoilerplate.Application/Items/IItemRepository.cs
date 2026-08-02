using AjBoilerplate.Application.Common;
using AjBoilerplate.Domain.Items;

namespace AjBoilerplate.Application.Items;

/// <summary>SAMPLE SLICE. Persistence port for <see cref="Item"/>; implemented in Infrastructure.
/// The Application layer never sees a <c>DbContext</c>.</summary>
public interface IItemRepository
{
    /// <summary>One page of items, newest first, optionally filtered by
    /// <paramref name="search"/> (case-insensitive substring of name or description). Paging and
    /// counting both happen in the database — never in memory.</summary>
    Task<PagedResult<Item>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken);

    Task<Item?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(Item item, CancellationToken cancellationToken);

    void Remove(Item item);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
