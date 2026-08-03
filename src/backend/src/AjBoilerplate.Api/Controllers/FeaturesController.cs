using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Features;
using AjBoilerplate.Contracts.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// "What's new" feature announcements. A client calls <c>GET unack</c> on every route change with the
/// path it is on; the API answers with any active announcement this user has not dismissed that is
/// bound to a matching path prefix. Dismissal round-trips through <c>POST ack</c> and is one-way per
/// user — once acknowledged, an announcement never surfaces for them again, on any device.
///
/// Both endpoints require nothing more than a signed-in caller: <see cref="Policies.ReadAccess"/> is
/// this project's widest policy, satisfied by every recognised role. Dismissal is deliberately NOT
/// behind <see cref="Policies.WriteAccess"/> — it writes a row about the CALLER, not about a business
/// record, and a read-only user must still be able to close the popup.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.ReadAccess)]
[Route("api/v1/features")]
[Produces("application/json")]
public sealed class FeaturesController : ControllerBase
{
    private readonly IFeatureAnnouncementService _features;

    public FeaturesController(IFeatureAnnouncementService features) => _features = features;

    /// <summary>
    /// Active announcements the current user has not acknowledged yet whose page list matches
    /// <paramref name="path"/> — or whose page list is empty, which matches every route. Returns an
    /// empty array when there is nothing to show.
    /// </summary>
    /// <param name="path">
    /// The path the client is currently on, e.g. <c>/reports/monthly</c>. Any query string or
    /// fragment is ignored, and <c>.</c>/<c>..</c> segments are resolved before matching, so a path
    /// is always compared as it actually resolves. Defaults to <c>/</c> when omitted.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("unack")]
    [ProducesResponseType(typeof(IReadOnlyList<FeatureAnnouncementResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnacknowledged([FromQuery] string? path = null, CancellationToken ct = default)
    {
        var announcements = await _features.GetUnacknowledgedAsync(new UnacknowledgedFeaturesQuery(path), ct);
        return Ok(announcements.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Marks one or more announcements as seen by the current user. Idempotent — already-acknowledged
    /// ids are silently skipped, so dismissing twice is a success, not a conflict. Call it on every
    /// close path (the confirm button, the close icon, Escape).
    /// </summary>
    /// <param name="request">The ids to acknowledge. An empty array is an accepted no-op.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("ack")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Acknowledge(AcknowledgeFeaturesRequest request, CancellationToken ct)
    {
        await _features.AcknowledgeAsync(new AcknowledgeFeaturesCommand(request.FeatureIds ?? []), ct);
        return NoContent();
    }

    private static FeatureAnnouncementResponse ToResponse(FeatureAnnouncementDto announcement) => new(
        announcement.Id,
        announcement.Key,
        announcement.TitleEn,
        announcement.TitleAr,
        announcement.BodyEn,
        announcement.BodyAr,
        announcement.DisplayOrder,
        announcement.CreatedAt);
}
