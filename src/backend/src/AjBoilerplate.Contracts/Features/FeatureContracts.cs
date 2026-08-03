namespace AjBoilerplate.Contracts.Features;

/// <summary>
/// A "what's new" announcement the current user has not dismissed yet. The only shape that crosses
/// the API boundary for announcements; the EF entity is never serialised.
/// </summary>
/// <param name="Id">Send this back to <c>POST /api/v1/features/ack</c> to dismiss it.</param>
/// <param name="Key">Stable machine identifier, e.g. <c>search-v2</c>. Not for display.</param>
/// <param name="TitleEn">English title.</param>
/// <param name="TitleAr">Arabic title; null means "fall back to the English title".</param>
/// <param name="BodyEn">English body.</param>
/// <param name="BodyAr">Arabic body; null means "fall back to the English body".</param>
/// <param name="DisplayOrder">Presentation order when several are pending; lower shows first.</param>
/// <param name="CreatedAt">When the announcement was created, UTC.</param>
public sealed record FeatureAnnouncementResponse(
    Guid Id,
    string Key,
    string TitleEn,
    string? TitleAr,
    string BodyEn,
    string? BodyAr,
    int DisplayOrder,
    DateTimeOffset CreatedAt);

/// <summary>Dismissal request body.</summary>
/// <param name="FeatureIds">
/// The announcements to mark as seen — normally every id the last lookup returned, sent in one
/// request when the user closes the popup. Idempotent: ids already acknowledged are silently
/// skipped, and an empty array is an accepted no-op.
/// </param>
public sealed record AcknowledgeFeaturesRequest(IReadOnlyList<Guid>? FeatureIds);
