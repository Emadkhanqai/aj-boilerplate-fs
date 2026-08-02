using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IOutboxRepository"/>.</summary>
public sealed class OutboxRepository : IOutboxRepository
{
    private readonly AppDbContext _context;

    public OutboxRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingBatchAsync(int maxCount, CancellationToken cancellationToken) =>
        await _context.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(CancellationToken cancellationToken) =>
        await _context.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Failed)
            .OrderByDescending(m => m.OccurredAtUtc)
            .ToListAsync(cancellationToken);

    public Task<OutboxMessage?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        await _context.OutboxMessages.AddAsync(message, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
