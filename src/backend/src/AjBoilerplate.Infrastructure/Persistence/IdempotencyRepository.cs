using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IIdempotencyRepository"/>.</summary>
public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly AppDbContext _context;

    public IdempotencyRepository(AppDbContext context) => _context = context;

    public Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken) =>
        _context.IdempotencyRecords.FirstOrDefaultAsync(
            r => r.Scope == scope && r.Key == key, cancellationToken);

    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken) =>
        await _context.IdempotencyRecords.AddAsync(record, cancellationToken);

    public void Remove(IdempotencyRecord record) => _context.IdempotencyRecords.Remove(record);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);

    public void ClearChangeTracking() => _context.ChangeTracker.Clear();
}
