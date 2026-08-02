using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.Messaging.Inbox;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IInboxRepository"/>.</summary>
public sealed class InboxRepository : IInboxRepository
{
    private readonly AppDbContext _context;

    public InboxRepository(AppDbContext context) => _context = context;

    public Task<InboxMessage?> FindBySourceEventIdAsync(string sourceEventId, CancellationToken cancellationToken) =>
        _context.InboxMessages.FirstOrDefaultAsync(m => m.SourceEventId == sourceEventId, cancellationToken);

    public async Task AddAsync(InboxMessage message, CancellationToken cancellationToken) =>
        await _context.InboxMessages.AddAsync(message, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);

    public void ClearChangeTracking() => _context.ChangeTracker.Clear();
}
