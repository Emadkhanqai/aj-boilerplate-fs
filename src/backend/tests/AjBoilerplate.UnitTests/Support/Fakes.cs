using System.Reflection;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Domain.Common;
using AjBoilerplate.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.UnitTests.Support;

/// <summary>A clock frozen at a caller-chosen instant, so a test can assert on timestamps.</summary>
public sealed class FixedClock : IClock
{
    private DateTimeOffset _now;

    public FixedClock(DateTimeOffset now) => _now = now;

    public DateTime UtcNow => _now.UtcDateTime;

    public DateTimeOffset UtcNowOffset => _now;

    /// <summary>Moves the clock forward, so a test can distinguish a create timestamp from an
    /// update one.</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// An in-memory <see cref="IItemRepository"/>. Deliberately a hand-written fake rather than a mock:
/// the service's interesting behaviour is stateful (save, then read back the token the store
/// issued), which a per-call mock setup models badly.
///
/// It stands in for SQL Server closely enough to exercise the handler, including issuing a
/// <c>rowversion</c> on save. It is NOT a substitute for the real thing — that the database really
/// does issue and enforce that token is proven in <c>ConcurrencyAndConstraintTests</c>, against a
/// containerised SQL Server.
/// </summary>
public sealed class FakeItemRepository : IItemRepository
{
    // AuditedEntity.RowVersion has a private setter because nothing in the application may assign a
    // token — only the database does. EF Core writes it through that same private setter from its
    // materializer; this fake stands in for exactly that, so it reaches it the same way.
    private static readonly PropertyInfo RowVersionProperty =
        typeof(AuditedEntity).GetProperty(nameof(AuditedEntity.RowVersion))!;

    private static long _tokenSequence;

    private readonly List<Item> _items = [];

    /// <summary>How many times the service asked for a save — enough to assert that a rejected
    /// update never reached persistence.</summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// When set, the next save throws <see cref="DbUpdateConcurrencyException"/>, standing in for
    /// another transaction committing between this caller's read and its write — the one case the
    /// service's own pre-check cannot catch.
    /// </summary>
    public bool ThrowConcurrencyOnNextSave { get; set; }

    public IReadOnlyList<Item> Items => _items;

    public Task<PagedResult<Item>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        var query = _items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(i =>
                i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (i.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var matched = query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
        var pageItems = matched.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<Item>(pageItems, matched.Count, page, pageSize));
    }

    public Task<Item?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_items.Find(i => i.Id == id));

    public Task AddAsync(Item item, CancellationToken cancellationToken)
    {
        _items.Add(item);
        return Task.CompletedTask;
    }

    public void Remove(Item item) => _items.Remove(item);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (ThrowConcurrencyOnNextSave)
        {
            ThrowConcurrencyOnNextSave = false;
            throw new DbUpdateConcurrencyException("Simulated lost optimistic-concurrency race.");
        }

        SaveCount++;
        foreach (var item in _items)
        {
            StampRowVersion(item);
        }

        return Task.CompletedTask;
    }

    /// <summary>Seeds an item directly, bypassing the service, as if it had already been inserted.</summary>
    public Item Seed(string name, ItemStatus status, DateTimeOffset createdAt)
    {
        var item = Item.Create(Guid.NewGuid(), name, null, status, createdAt);
        StampRowVersion(item);
        _items.Add(item);
        return item;
    }

    // Simulates what SQL Server does on every INSERT and UPDATE: issue a fresh 8-byte token.
    private static void StampRowVersion(Item item) =>
        RowVersionProperty.SetValue(item, BitConverter.GetBytes(Interlocked.Increment(ref _tokenSequence)));
}
