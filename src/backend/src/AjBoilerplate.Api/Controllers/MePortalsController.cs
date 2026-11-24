using AjBoilerplate.Application.Portals;
using AjBoilerplate.Contracts.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// Which portals the signed-in person may open, for a portals menu. The caller's own capabilities decide it:
/// the route takes no parameter, so asking about someone else is not expressible.
///
/// <para>
/// <b>A configured list, not a catalogue.</b> On the USP platform this answer comes from USP-Core's portal
/// catalogue and the directory's seats. The boilerplate has neither, so the source is the <c>Portals</c>
/// configuration list, each entry optionally requiring <c>read</c>, <c>write</c> or <c>administer</c>
/// (see <c>ConfigurationPortalCatalogue</c>). The response keeps USP-Core's <c>portalKeys</c> field, so a menu
/// written against either works against both.
/// </para>
/// <para>
/// Bare <c>[Authorize]</c>, like <c>/me</c>: gating it on a role would answer 403 to exactly the caller who has
/// no role yet and needs the honest answer, an empty list.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/me/portals")]
[Produces("application/json")]
public sealed class MePortalsController : ControllerBase
{
    private readonly IPortalAccessService _portals;

    public MePortalsController(IPortalAccessService portals) => _portals = portals;

    /// <summary>The portals the caller may open, in the order they are configured.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MePortalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Get()
    {
        var portals = _portals.ForCaller();
        return Ok(new MePortalsResponse(
            portals.Select(p => p.Key).ToList(),
            portals.Select(p => new PortalResponse(p.Key, p.Name, p.Url)).ToList()));
    }
}
