using AjBoilerplate.Application.Common;

namespace AjBoilerplate.Application.Identity;

/// <summary>Resolves the authoritative profile of the caller making the current request.</summary>
public interface ICurrentUserService
{
    /// <summary>
    /// The current caller's identity and effective permissions.
    ///
    /// Throws <see cref="ForbiddenException"/> when there is no real authenticated subject — the
    /// endpoint's <c>[Authorize]</c> already rejects that case, so this is defence in depth for any
    /// other entry point into the use case. Returning a profile for the anonymous pseudo-identity
    /// would hand a client a plausible-looking "who am I" answer for nobody.
    /// </summary>
    UserProfileDto GetProfile();
}

/// <inheritdoc cref="ICurrentUserService"/>
/// <remarks>
/// This is a pure projection of the current principal: no database, no cache, no clock. It is the
/// server's answer to "who am I and what may I do", and it is deliberately derived from the SAME
/// three pieces the authorization policies use — <see cref="IActorClaims"/> for identity,
/// <see cref="ApplicationRoles"/> for the role vocabulary, <see cref="RoleCapabilities"/> for what
/// those roles grant. Nothing here reads a request body, a query string, or a header the client
/// controls, which is what makes the answer trustworthy.
/// </remarks>
public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IActorClaims _claims;

    public CurrentUserService(IActorClaims claims) => _claims = claims;

    public UserProfileDto GetProfile()
    {
        var userId = _claims.Id;
        if (!ActorIdentifiers.IsRealUser(userId))
        {
            throw new ForbiddenException("An authenticated user is required.");
        }

        // Canonicalised HERE even though IActorClaims already promises canonical names, and even
        // though HttpContextActorClaims keeps that promise today. This is the boundary that decides
        // what a client is told it is, so the guarantee is made true by construction rather than by
        // trusting a comment on a port that another host is free to implement differently. It costs
        // one projection and is idempotent — Canonical maps a display name to itself — so running it
        // twice cannot change the answer, while skipping it would let a raw authorization-server key
        // ("editor") reach the wire the day someone writes a second IActorClaims.
        //
        // Unrecognised roles are dropped, not passed through: a role this application has no
        // capability table for grants nothing, so naming it in the profile would describe authority
        // that does not exist.
        var roles = _claims.Roles
            .Select(ApplicationRoles.Canonical)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new UserProfileDto(
            userId.Trim(),
            _claims.Name,
            _claims.Email,
            roles,
            UserCapabilitiesDto.From(RoleCapabilities.For(roles)));
    }
}
