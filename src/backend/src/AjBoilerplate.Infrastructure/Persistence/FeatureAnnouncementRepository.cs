using AjBoilerplate.Application.Features;
using AjBoilerplate.Domain.Features;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IFeatureAnnouncementRepository"/>.</summary>
public sealed class FeatureAnnouncementRepository : IFeatureAnnouncementRepository
{
    private readonly AppDbContext _context;

    public FeatureAnnouncementRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<FeatureAnnouncement>> ListActiveAsync(CancellationToken cancellationToken) =>
        // AsNoTracking: a read-only projection that runs on every client navigation, so there is no
        // reason to snapshot these rows into the change tracker.
        //
        // Ordering is (DisplayOrder, CreatedAt, Id). The first two are the documented contract; Id
        // breaks a remaining tie so two announcements created in the same tick with the same order
        // still have a defined, stable sequence rather than whatever the engine happens to return.
        await _context.FeatureAnnouncements
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListAcknowledgedIdsAsync(
        string userId, IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken)
    {
        if (featureIds.Count == 0)
        {
            return [];
        }

        // Scoped to the ids in play rather than "everything this user ever acknowledged": the result
        // is only used to subtract from that set, and the predicate seeks the (UserId, FeatureId)
        // unique index either way.
        return await _context.FeatureAcknowledgements
            .AsNoTracking()
            .Where(a => a.UserId == userId && featureIds.Contains(a.FeatureId))
            .Select(a => a.FeatureId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListExistingIdsAsync(
        IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken)
    {
        if (featureIds.Count == 0)
        {
            return [];
        }

        return await _context.FeatureAnnouncements
            .AsNoTracking()
            .Where(f => featureIds.Contains(f.Id))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAcknowledgementsAsync(
        IEnumerable<FeatureAcknowledgement> acknowledgements, CancellationToken cancellationToken) =>
        await _context.FeatureAcknowledgements.AddRangeAsync(acknowledgements, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
