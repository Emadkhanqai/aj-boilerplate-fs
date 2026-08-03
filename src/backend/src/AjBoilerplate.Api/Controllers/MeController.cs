using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.Api.Controllers;

/// <summary>
/// "Who am I, and what may I do?" — the endpoint a signed-in client calls once per session to learn
/// its own permissions, so the SERVER decides what the user may do rather than the browser guessing
/// from a token it decoded itself.
///
/// <para>
/// <b>This is authenticated but deliberately NOT behind a role policy.</b> Every other controller
/// carries at least <see cref="Identity.Policies.ReadAccess"/>; this one carries a bare
/// <c>[Authorize]</c>, and the difference is load-bearing. <c>ReadAccess</c> is satisfied only by a
/// recognised role, so gating this endpoint on it would answer 403 to exactly the caller who most
/// needs an answer: someone authenticated whose account has not been granted a role yet. The client
/// would then be unable to distinguish "you have no permissions" from "the permissions endpoint is
/// broken", and the honest answer — a 200 with every capability false — is the one that lets it
/// render a correct "your account is not provisioned" state. Requiring a capability in order to ask
/// which capabilities you hold is circular.
/// </para>
///
/// <para>
/// It is still a protected endpoint: an anonymous caller gets the same enveloped 401 as anywhere
/// else, because there is no principal to describe.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/me")]
[Produces("application/json")]
public sealed class MeController : ControllerBase
{
    private readonly ICurrentUserService _currentUser;

    public MeController(ICurrentUserService currentUser) => _currentUser = currentUser;

    /// <summary>
    /// The current caller's identity, roles, and capabilities. Everything is derived from the
    /// authenticated principal; no field is influenced by anything the client sends.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Get()
    {
        var profile = _currentUser.GetProfile();

        return Ok(new UserProfileResponse(
            profile.UserId,
            profile.DisplayName,
            profile.Email,
            profile.Roles,
            new UserCapabilitiesResponse(
                profile.Capabilities.CanView,
                profile.Capabilities.CanCreate,
                profile.Capabilities.CanEdit,
                profile.Capabilities.CanDelete,
                profile.Capabilities.CanAdminister)));
    }
}
