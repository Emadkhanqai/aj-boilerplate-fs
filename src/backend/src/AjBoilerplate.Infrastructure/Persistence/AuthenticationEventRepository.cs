using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Application.Persistence;
using AjBoilerplate.Domain.AuthenticationHistory;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

public sealed class AuthenticationEventRepository : IAuthenticationEventRepository
{
    private readonly AppDbContext _context;

    public AuthenticationEventRepository(AppDbContext context) => _context = context;

    public async Task<bool> TryAddAsync(AuthenticationEvent authenticationEvent, CancellationToken cancellationToken)
    {
        _context.AuthenticationEvents.Add(authenticationEvent);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (SqlUniqueConstraintViolation.Matches(ex))
        {
            // Two first requests of one session raced and the unique index let one through.
            Forget(authenticationEvent);
            return false;
        }
        catch
        {
            Forget(authenticationEvent);
            throw;
        }
    }

    public async Task<(IReadOnlyList<AuthenticationEvent> Items, int Total)> ListAsync(
        AuthenticationHistoryFilter filter, CancellationToken cancellationToken)
    {
        var query = _context.AuthenticationEvents.AsNoTracking().AsQueryable();
        if (filter.UserId is not null)
        {
            query = query.Where(e => e.Subject == filter.UserId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(e => e.StartedAt >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(e => e.StartedAt < to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Sort(query, filter.SortField, filter.SortDescending)
            // Ties break the same way whatever the column, so paging is stable.
            .ThenByDescending(e => e.StartedAt)
            .ThenBy(e => e.SessionId)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    // NULLs (an open session has no ending and no duration) order first ascending and last descending, as
    // SQL Server does.
    private static IOrderedQueryable<AuthenticationEvent> Sort(
        IQueryable<AuthenticationEvent> query, AuthenticationHistorySortField field, bool descending) => field switch
        {
            AuthenticationHistorySortField.User => Order(query, e => e.DisplayName ?? e.Subject, descending),
            AuthenticationHistorySortField.SessionEnded => Order(query, e => e.EndedAt, descending),
            AuthenticationHistorySortField.Duration =>
                Order(query, e => EF.Functions.DateDiffSecond(e.StartedAt, e.EndedAt), descending),

            // Failure sorts before Success, as the words do.
            AuthenticationHistorySortField.Result =>
                Order(query, e => e.EndReason == SessionEndReasons.SignedOutBySystem ? 0 : 1, descending),
            AuthenticationHistorySortField.EndReason => Order(query, e => e.EndReason, descending),
            _ => Order(query, e => e.StartedAt, descending),
        };

    private static IOrderedQueryable<AuthenticationEvent> Order<TKey>(
        IQueryable<AuthenticationEvent> query, System.Linq.Expressions.Expression<Func<AuthenticationEvent, TKey>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : query.OrderBy(key);

    public async Task<IReadOnlyList<AuthenticationHistoryUser>> UsersAsync(CancellationToken cancellationToken) =>
        (await _context.AuthenticationEvents.AsNoTracking()
            .GroupBy(e => e.Subject)
            .Select(g => new { UserId = g.Key, Name = g.Max(e => e.DisplayName) })
            .ToListAsync(cancellationToken))
        .Select(u => new AuthenticationHistoryUser(u.UserId, u.Name))
        .OrderBy(u => u.UserDisplayName ?? u.UserId, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task<SessionEndWrite> EndAsync(
        string sessionId, Guid eventId, string reason, string? reasonDetail, DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        var detail = AuthenticationEvent.IsSlug(reasonDetail) ? reasonDetail : null;

        // ONE conditional UPDATE decides it, never read-then-write: two deliveries of one event racing here
        // cannot both match `EndedAt IS NULL`.
        var changed = await _context.AuthenticationEvents
            .Where(e => e.SessionId == sessionId && e.EndedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.EndedAt, endedAt)
                    .SetProperty(e => e.EndReason, reason)
                    .SetProperty(e => e.EndReasonDetail, detail)
                    .SetProperty(e => e.EndEventId, eventId),
                cancellationToken);
        if (changed > 0)
        {
            return SessionEndWrite.Ended;
        }

        // Nothing matched: either it has already ended (a retry — a duplicate, even if the same event) or
        // there is no such session.
        return await _context.AuthenticationEvents.AnyAsync(e => e.SessionId == sessionId, cancellationToken)
            ? SessionEndWrite.AlreadyEnded
            : SessionEndWrite.UnknownSession;
    }

    // The context is shared with the rest of the request. A row whose save failed would otherwise be
    // sent again by the request's own SaveChanges and fail that request instead.
    private void Forget(AuthenticationEvent authenticationEvent) =>
        _context.Entry(authenticationEvent).State = EntityState.Detached;
}
