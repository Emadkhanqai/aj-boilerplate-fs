using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Features;
using AjBoilerplate.Domain.Common;
using AjBoilerplate.Domain.Features;

namespace AjBoilerplate.UnitTests.Support;

/// <summary>An <see cref="ICurrentActor"/> a test can point at any identity — including the
/// pseudo-identities that mean "nobody is signed in".</summary>
public sealed class StubCurrentActor : ICurrentActor
{
    public StubCurrentActor(string id) => Id = id;

    public string Id { get; set; }

    public Actor GetActor() => new(Id, "Test User", "Viewer");
}

/// <summary>
/// An in-memory <see cref="IFeatureAnnouncementRepository"/>. A hand-written fake rather than a mock
/// because the interesting behaviour is stateful: acknowledge, then read back and prove nothing was
/// written the second time.
///
/// It does NOT enforce the (UserId, FeatureId) unique index — that the database really enforces it is
/// proved against a containerised SQL Server in the integration suite. Here the point is that the
/// application never reaches it.
/// </summary>
public sealed class FakeFeatureAnnouncementRepository : IFeatureAnnouncementRepository
{
    private readonly List<FeatureAnnouncement> _announcements = [];
    private readonly List<FeatureAcknowledgement> _acknowledgements = [];

    /// <summary>How many times the service asked for a save — enough to assert that a repeat
    /// acknowledgement never reached persistence at all.</summary>
    public int SaveCount { get; private set; }

    public IReadOnlyList<FeatureAcknowledgement> Acknowledgements => _acknowledgements;

    public FeatureAnnouncement Seed(
        string key,
        string? pagesJson = null,
        bool isActive = true,
        int displayOrder = 0,
        DateTimeOffset? createdAt = null)
    {
        var announcement = FeatureAnnouncement.Create(
            Guid.NewGuid(),
            key,
            $"{key} title",
            null,
            $"{key} body",
            null,
            pagesJson,
            isActive,
            displayOrder,
            createdAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        _announcements.Add(announcement);
        return announcement;
    }

    public void SeedAcknowledgement(string userId, Guid featureId, DateTimeOffset acknowledgedAt) =>
        _acknowledgements.Add(FeatureAcknowledgement.Create(Guid.NewGuid(), userId, featureId, acknowledgedAt));

    public Task<IReadOnlyList<FeatureAnnouncement>> ListActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeatureAnnouncement>>(_announcements
            .Where(a => a.IsActive)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToList());

    public Task<IReadOnlyList<Guid>> ListAcknowledgedIdsAsync(
        string userId, IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(_acknowledgements
            .Where(a => string.Equals(a.UserId, userId, StringComparison.Ordinal) && featureIds.Contains(a.FeatureId))
            .Select(a => a.FeatureId)
            .ToList());

    public Task<IReadOnlyList<Guid>> ListExistingIdsAsync(
        IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(_announcements
            .Where(a => featureIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToList());

    public Task AddAcknowledgementsAsync(
        IEnumerable<FeatureAcknowledgement> acknowledgements, CancellationToken cancellationToken)
    {
        _acknowledgements.AddRange(acknowledgements);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
