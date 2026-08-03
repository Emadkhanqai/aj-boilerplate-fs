using AjBoilerplate.Domain.Features;

namespace AjBoilerplate.Application.Features;

/// <summary>Persistence port for the "what's new" announcements; implemented in Infrastructure. The
/// Application layer never sees a <c>DbContext</c>.</summary>
public interface IFeatureAnnouncementRepository
{
    /// <summary>
    /// Every active announcement, ordered by <c>DisplayOrder</c> then <c>CreatedAt</c> — the order a
    /// client presents them in. Ordering happens in the database, on the index that exists for it;
    /// page-list matching does not, because it needs the JSON array parsed and the set is a handful
    /// of rows.
    /// </summary>
    Task<IReadOnlyList<FeatureAnnouncement>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="featureIds"/> this user has already acknowledged.</summary>
    Task<IReadOnlyList<Guid>> ListAcknowledgedIdsAsync(
        string userId, IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="featureIds"/> name an announcement that actually exists.</summary>
    Task<IReadOnlyList<Guid>> ListExistingIdsAsync(
        IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken);

    Task AddAcknowledgementsAsync(
        IEnumerable<FeatureAcknowledgement> acknowledgements, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
