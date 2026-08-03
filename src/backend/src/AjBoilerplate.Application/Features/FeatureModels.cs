namespace AjBoilerplate.Application.Features;

/// <summary>
/// The Application layer's own read model for an announcement — mapped to
/// <c>AjBoilerplate.Contracts.Features.FeatureAnnouncementResponse</c> by the Api layer, never
/// returned raw. The Application assembly does not reference the wire contracts (the architecture
/// tests enforce it), so a breaking API change cannot ripple into the use cases.
/// </summary>
/// <param name="Id">Round-tripped back on acknowledgement.</param>
/// <param name="Key">Stable machine identifier, e.g. <c>"search-v2"</c>.</param>
/// <param name="TitleEn">English title.</param>
/// <param name="TitleAr">Arabic title; null means "fall back to English".</param>
/// <param name="BodyEn">English body.</param>
/// <param name="BodyAr">Arabic body; null means "fall back to English".</param>
/// <param name="DisplayOrder">Lower shows first.</param>
/// <param name="CreatedAt">When the announcement was created, UTC.</param>
public sealed record FeatureAnnouncementDto(
    Guid Id,
    string Key,
    string TitleEn,
    string? TitleAr,
    string BodyEn,
    string? BodyAr,
    int DisplayOrder,
    DateTimeOffset CreatedAt);

/// <summary>
/// Which announcements to surface, for the route the caller is currently on.
/// </summary>
/// <param name="Path">
/// The client's current URL path. Null, blank, a query string, a fragment, and <c>.</c>/<c>..</c>
/// segments are all handled — it is canonicalised before any prefix comparison.
/// </param>
public sealed record UnacknowledgedFeaturesQuery(string? Path);

/// <summary>
/// Marks announcements as seen by the current user. Idempotent: ids the user has already
/// acknowledged are silently skipped, so a double dismiss or a retried request is a no-op rather
/// than an error.
/// </summary>
public sealed record AcknowledgeFeaturesCommand(IReadOnlyList<Guid> FeatureIds)
{
    /// <summary>
    /// The most ids one request may acknowledge. A client only ever sends back what a single lookup
    /// handed it — a handful — so this bounds a hostile request without constraining a real one.
    /// </summary>
    public const int MaxFeatureIds = 200;
}
