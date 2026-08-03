using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Domain.Features;
using FluentValidation;

namespace AjBoilerplate.Application.Features;

/// <summary>
/// The "what's new" use cases. Resolves which announcements the CURRENT user should see on a given
/// route, and records their dismissal so the same one never surfaces again.
/// </summary>
public interface IFeatureAnnouncementService
{
    /// <summary>
    /// Active announcements the current user has not acknowledged AND whose page list matches
    /// <c>query.Path</c> (an empty page list matches every route). Ordered by <c>DisplayOrder</c>,
    /// then <c>CreatedAt</c>. Empty when there is nothing to surface.
    /// </summary>
    Task<IReadOnlyList<FeatureAnnouncementDto>> GetUnacknowledgedAsync(
        UnacknowledgedFeaturesQuery query, CancellationToken cancellationToken);

    /// <summary>Marks the given announcements as seen by the current user. Idempotent.</summary>
    Task AcknowledgeAsync(AcknowledgeFeaturesCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFeatureAnnouncementService"/>
public sealed class FeatureAnnouncementService : IFeatureAnnouncementService
{
    private readonly IFeatureAnnouncementRepository _announcements;
    private readonly ICurrentActor _currentActor;
    private readonly IClock _clock;
    private readonly IValidator<UnacknowledgedFeaturesQuery> _queryValidator;
    private readonly IValidator<AcknowledgeFeaturesCommand> _acknowledgeValidator;

    public FeatureAnnouncementService(
        IFeatureAnnouncementRepository announcements,
        ICurrentActor currentActor,
        IClock clock,
        IValidator<UnacknowledgedFeaturesQuery> queryValidator,
        IValidator<AcknowledgeFeaturesCommand> acknowledgeValidator)
    {
        _announcements = announcements;
        _currentActor = currentActor;
        _clock = clock;
        _queryValidator = queryValidator;
        _acknowledgeValidator = acknowledgeValidator;
    }

    public async Task<IReadOnlyList<FeatureAnnouncementDto>> GetUnacknowledgedAsync(
        UnacknowledgedFeaturesQuery query, CancellationToken cancellationToken)
    {
        await _queryValidator.ValidateAndThrowAsync(query, cancellationToken);
        var userId = RequireAuthenticatedUser();

        var active = await _announcements.ListActiveAsync(cancellationToken);
        if (active.Count == 0)
        {
            return [];
        }

        // Two narrow reads rather than one join: the active set is tiny and already ordered by the
        // index, and the second query is a keyset lookup on (UserId, FeatureId) — the same unique
        // index that guarantees one acknowledgement per user.
        var acknowledged = await _announcements.ListAcknowledgedIdsAsync(
            userId, active.Select(a => a.Id).ToList(), cancellationToken);
        var acknowledgedIds = acknowledged.ToHashSet();

        // Page-list matching happens IN MEMORY. Each announcement carries a handful of prefixes in a
        // JSON column, and matching one means resolving the caller's path first — work SQL Server
        // would do badly and could not index anyway.
        return active
            .Where(a => !acknowledgedIds.Contains(a.Id) && a.Targets(query.Path))
            .Select(ToDto)
            .ToList();
    }

    public async Task AcknowledgeAsync(AcknowledgeFeaturesCommand command, CancellationToken cancellationToken)
    {
        await _acknowledgeValidator.ValidateAndThrowAsync(command, cancellationToken);
        var userId = RequireAuthenticatedUser();

        var requested = command.FeatureIds.Distinct().ToList();
        if (requested.Count == 0)
        {
            return;
        }

        // IDEMPOTENCY IS COMPUTED HERE, not delegated to the unique index. Filtering the ids this
        // user already acknowledged means a double-click, a retried request, or a client that acks
        // the same carousel twice writes nothing and returns success — rather than raising a
        // unique-constraint violation that would have to be caught, classified by SQL error number,
        // and translated back into the success it always was. The index stays as the backstop for
        // the one case this cannot cover: two requests racing past this check at the same instant.
        var alreadyAcknowledged = (await _announcements.ListAcknowledgedIdsAsync(userId, requested, cancellationToken))
            .ToHashSet();

        // An id naming no announcement is dropped rather than inserted. Without this the foreign key
        // would reject the whole batch with a 500 — including in a legitimate race, where an
        // announcement was deleted (cascading its acknowledgements away) between the lookup that
        // handed the client these ids and the dismissal that sends them back.
        var known = (await _announcements.ListExistingIdsAsync(requested, cancellationToken)).ToHashSet();

        var now = _clock.UtcNowOffset;
        var toInsert = requested
            .Where(id => known.Contains(id) && !alreadyAcknowledged.Contains(id))
            .Select(id => FeatureAcknowledgement.Create(Guid.NewGuid(), userId, id, now))
            .ToList();

        if (toInsert.Count == 0)
        {
            return;
        }

        await _announcements.AddAcknowledgementsAsync(toInsert, cancellationToken);
        await _announcements.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The current user's identifier, or a refusal.
    ///
    /// The endpoint's authorization policy already rejects an anonymous caller, so this is defence in
    /// depth for any other entry point into the use case: acknowledgement is recorded PER USER, and
    /// attributing rows to a shared pseudo-identity would silently mark an announcement dismissed for
    /// everyone who is not signed in.
    /// </summary>
    private string RequireAuthenticatedUser()
    {
        var actorId = _currentActor.GetActor().Id;
        if (!ActorIdentifiers.IsRealUser(actorId))
        {
            throw new ForbiddenException("An authenticated user is required.");
        }

        return actorId.Trim();
    }

    private static FeatureAnnouncementDto ToDto(FeatureAnnouncement announcement) => new(
        announcement.Id,
        announcement.Key,
        announcement.TitleEn,
        announcement.TitleAr,
        announcement.BodyEn,
        announcement.BodyAr,
        announcement.DisplayOrder,
        announcement.CreatedAt);
}
