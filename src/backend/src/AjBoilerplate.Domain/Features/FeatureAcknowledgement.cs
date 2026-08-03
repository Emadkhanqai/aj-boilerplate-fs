using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.Features;

/// <summary>
/// One user's dismissal of one <see cref="FeatureAnnouncement"/>. Its existence is the whole
/// mechanism: once the row is there, that announcement is never returned to that user again, on any
/// device.
///
/// Unique on (<see cref="UserId"/>, <see cref="FeatureId"/>). The application computes idempotency
/// itself by filtering out ids the user has already acknowledged, so a double-click or a retried
/// request never reaches that index — but the index remains the authoritative backstop, because two
/// concurrent requests can both pass an application-level check.
/// </summary>
public sealed class FeatureAcknowledgement : AuditedEntity
{
    /// <summary>
    /// Maximum length of <see cref="UserId"/>. It holds the identity provider's subject identifier
    /// verbatim (this codebase's <see cref="Actor.Id"/> is an opaque string from an external IdP, not
    /// a local key), so it is sized for a long opaque subject rather than for a GUID.
    /// </summary>
    public const int MaxUserIdLength = 128;

    private FeatureAcknowledgement() => UserId = string.Empty;

    private FeatureAcknowledgement(Guid id, string userId, Guid featureId, DateTimeOffset nowUtc)
    {
        Id = id;
        UserId = userId;
        FeatureId = featureId;
        AcknowledgedAt = nowUtc;
        MarkCreated(nowUtc);
    }

    public Guid Id { get; private set; }

    /// <summary>The authenticated caller's identifier, as issued by the identity provider.</summary>
    public string UserId { get; private set; }

    public Guid FeatureId { get; private set; }

    /// <summary>When the user dismissed the announcement.</summary>
    public DateTimeOffset AcknowledgedAt { get; private set; }

    /// <summary>The acknowledged announcement. Deleting it cascades to this row.</summary>
    public FeatureAnnouncement? Feature { get; private set; }

    public static FeatureAcknowledgement Create(Guid id, string userId, Guid featureId, DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An acknowledgement's id is required.");
        }

        if (featureId == Guid.Empty)
        {
            throw new DomainException("An acknowledgement must name the announcement it dismisses.");
        }

        var trimmed = userId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("An acknowledgement must name the user who dismissed the announcement.");
        }

        if (trimmed.Length > MaxUserIdLength)
        {
            throw new DomainException($"A user identifier cannot exceed {MaxUserIdLength} characters.");
        }

        return new FeatureAcknowledgement(id, trimmed, featureId, nowUtc);
    }
}
