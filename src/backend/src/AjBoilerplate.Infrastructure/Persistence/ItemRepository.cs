using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>SAMPLE SLICE. EF Core implementation of <see cref="IItemRepository"/>.</summary>
public sealed class ItemRepository : IItemRepository
{
    private readonly AppDbContext _context;

    public ItemRepository(AppDbContext context) => _context = context;

    public async Task<PagedResult<Item>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        // AsNoTracking: this is a read-only projection, so EF Core has no reason to snapshot every
        // row into the change tracker.
        var query = _context.Items.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // EF.Functions.Like translates to a real SQL LIKE — never a client-side Contains, which
            // would pull the whole table into memory to filter it.
            var pattern = $"%{Escape(search)}%";
            query = query.Where(i =>
                EF.Functions.Like(i.Name, pattern)
                || (i.Description != null && EF.Functions.Like(i.Description, pattern)));
        }

        // Count and page in the database. Ordering is (CreatedAt desc, Id) rather than CreatedAt
        // alone: two items created in the same tick would otherwise have no defined relative order,
        // and an unstable sort makes paging skip and repeat rows.
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Item>(items, total, page, pageSize);
    }

    public Task<Item?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task AddAsync(Item item, CancellationToken cancellationToken) =>
        await _context.Items.AddAsync(item, cancellationToken);

    public void Remove(Item item) => _context.Items.Remove(item);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Escapes the LIKE metacharacters so a search term containing <c>%</c> or <c>_</c> matches those
    /// characters literally instead of turning into a wildcard the caller never asked for (a leading
    /// <c>%</c> in user input otherwise forces a full scan and matches everything).
    /// </summary>
    private static string Escape(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);
}
