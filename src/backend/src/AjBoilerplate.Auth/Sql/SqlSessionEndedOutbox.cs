using Microsoft.EntityFrameworkCore;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Sql;

/// <summary>
/// <c>auth.SessionEndedOutbox</c>, the dispatcher's side . Rows are written by
/// <see cref="SqlSessionStore.EndAsync"/>; this claims and settles them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is optimistic and needs no lock service.</b> Up to four instances poll the same
/// table. Each reads candidate rows, then moves a row's <c>NextAttemptAt</c> forward by the lease
/// ON THE CONDITION that it still holds the value just read. Exactly one instance's update affects
/// the row; the others affect nothing and skip it. No Redis lock is involved, so a Redis outage
/// cannot stop delivery, and a claimant that dies simply lets its lease run out.
/// </para>
/// <para>
/// <c>datetimeoffset</c> keeps 100 ns, the same resolution as <see cref="DateTimeOffset"/>, so the
/// value read back compares equal to the value stored.
/// </para>
/// </remarks>
internal sealed class SqlSessionEndedOutbox : ISessionEndedOutbox
{
    private readonly IDbContextFactory<AuthDbContext> _contexts;
    private readonly TimeProvider _time;

    public SqlSessionEndedOutbox(IDbContextFactory<AuthDbContext> contexts, TimeProvider time)
    {
        _contexts = contexts;
        _time = time;
    }

    public async Task<IReadOnlyList<SessionEndedEvent>> ClaimDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();

        var candidates = await db.SessionEndedOutbox
            .AsNoTracking()
            .Where(x => x.DeliveredAt == null && x.AbandonedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(max)
            .ToListAsync(cancellationToken);

        var claimed = new List<SessionEndedEvent>(candidates.Count);
        var leasedUntil = now + lease;
        foreach (var row in candidates)
        {
            var seen = row.NextAttemptAt;
            var affected = await db.SessionEndedOutbox
                .Where(x => x.EventId == row.EventId
                    && x.NextAttemptAt == seen
                    && x.DeliveredAt == null
                    && x.AbandonedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.NextAttemptAt, leasedUntil)
                          .SetProperty(x => x.Attempts, x => x.Attempts + 1),
                    cancellationToken);

            if (affected == 1)
            {
                claimed.Add(new SessionEndedEvent(
                    row.EventId,
                    row.SessionId,
                    row.ClientId,
                    row.Reason,
                    row.Detail,
                    row.OccurredAt,
                    row.SessionStartedAt,
                    row.CorrelationId,
                    row.EnqueuedAt,
                    row.Attempts + 1));
            }
        }

        return claimed;
    }

    public async Task MarkDeliveredAsync(Guid eventId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();
        await db.SessionEndedOutbox
            .Where(x => x.EventId == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveredAt, now).SetProperty(x => x.LastOutcome, Slug(outcome)), cancellationToken);
    }

    public async Task ScheduleRetryAsync(Guid eventId, DateTimeOffset nextAttemptAt, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await db.SessionEndedOutbox
            .Where(x => x.EventId == eventId && x.DeliveredAt == null && x.AbandonedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, nextAttemptAt).SetProperty(x => x.LastOutcome, Slug(outcome)), cancellationToken);
    }

    public async Task AbandonAsync(Guid eventId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();
        await db.SessionEndedOutbox
            .Where(x => x.EventId == eventId && x.DeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AbandonedAt, now).SetProperty(x => x.LastOutcome, Slug(outcome)), cancellationToken);
    }

    public async Task<int> PurgeSettledAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.SessionEndedOutbox
            .Where(x => (x.DeliveredAt != null && x.DeliveredAt <= before) || (x.AbandonedAt != null && x.AbandonedAt <= before))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>The column is <c>varchar(64)</c>; outcomes are slugs, so cutting one is safe.</summary>
    private static string Slug(string outcome) => outcome.Length <= 64 ? outcome : outcome[..64];
}
