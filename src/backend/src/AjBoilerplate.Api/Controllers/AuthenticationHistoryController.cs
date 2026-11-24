using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Contracts.AuthenticationHistory;
using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// Administration → Sign-in history: who signed in when, and how each session ended. One row per session
/// (the shape USP-Core and CAD settled on). Read-only: nothing writes here but the sign-in recorder and the
/// Auth service's session-ended receiver (<see cref="AuthBackendIntegrationController"/>).
/// <see cref="Policies.AdminAccess"/>, the same seats that open every other administrative surface.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.AdminAccess)]
[Route("api/v1/admin/authentication-history")]
[Produces("application/json")]
public sealed class AuthenticationHistoryController : ControllerBase
{
    private readonly IAuthenticationHistoryService _history;

    public AuthenticationHistoryController(IAuthenticationHistoryService history) => _history = history;

    /// <summary>
    /// Sessions, newest sign-in first, 20 per page. <c>userId</c> filters to one person; <c>from</c> (inclusive)
    /// and <c>to</c> (exclusive) bound the sign-in time. <c>sortBy</c> is one of <c>signedIn</c>, <c>user</c>,
    /// <c>sessionEnded</c>, <c>duration</c>, <c>result</c>, <c>endReason</c> and sorts across ALL pages;
    /// <c>sortDir</c> is <c>asc</c> or <c>desc</c> (sortBy alone: ascending). An open session has no ending or
    /// duration: SQL Server orders that NULL first ascending, last descending. Any unknown value is a 400.
    /// </summary>
    /// <param name="userId">A subject from <c>GET …/users</c>.</param>
    /// <param name="from">Earliest sign-in, inclusive.</param>
    /// <param name="to">Latest sign-in, exclusive.</param>
    /// <param name="sortBy">The column to sort on.</param>
    /// <param name="sortDir"><c>asc</c> or <c>desc</c>.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page, 1–100.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<AuthenticationSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? userId = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = Paging.DefaultPage,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var result = await _history.ListAsync(new AuthenticationHistoryQuery(userId, from, to, sortBy, sortDir, page, pageSize), ct);
        return Ok(new PagedResponse<AuthenticationSessionResponse>(
            result.Items.Select(s => new AuthenticationSessionResponse(
                s.Id, s.UserId, s.UserDisplayName, s.SignedInAtUtc, s.EndedAtUtc, s.EndReason, s.EndReasonDetail,
                s.Result, s.DurationSeconds)).ToList(),
            result.Total, result.Page, result.PageSize));
    }

    /// <summary>The people the history names, once each and A–Z, for the User filter.</summary>
    [HttpGet("users")]
    [ProducesResponseType(typeof(List<AuthenticationHistoryUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Users(CancellationToken ct) =>
        Ok((await _history.UsersAsync(ct)).Select(u => new AuthenticationHistoryUserResponse(u.UserId, u.UserDisplayName)).ToList());
}
