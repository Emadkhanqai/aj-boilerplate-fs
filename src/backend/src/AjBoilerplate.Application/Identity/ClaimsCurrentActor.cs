using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Application.Identity;

/// <summary>Builds the domain <see cref="Actor"/> from the authenticated principal's claims. The
/// only place a claims-derived identity becomes a domain type.</summary>
public sealed class ClaimsCurrentActor : ICurrentActor
{
    private readonly IActorClaims _claims;

    public ClaimsCurrentActor(IActorClaims claims) => _claims = claims;

    public Actor GetActor() => new(
        _claims.Id,
        _claims.Name,
        _claims.Role,
        string.IsNullOrWhiteSpace(_claims.Email) ? null : _claims.Email,
        _claims.Groups);
}

/// <summary>
/// The placeholder actor used only where no authentication pipeline is wired up at all (a console
/// tool, a design-time host, a unit test that does not care who acted). <c>AddApiAuthentication</c>
/// replaces this registration with <see cref="ClaimsCurrentActor"/>, so a running API never uses it.
/// It grants no role, so anything that authorizes on the actor's role fails closed.
/// </summary>
public sealed class SystemCurrentActor : ICurrentActor
{
    public Actor GetActor() => new(ActorIdentifiers.System, "System", string.Empty);
}
