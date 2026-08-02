namespace AjBoilerplate.Application.Identity;

/// <summary>
/// The authenticated caller's identity, expressed only as primitive strings.
///
/// This exists so the Api layer never has to construct a domain <c>Actor</c> itself: the Api
/// implements this over <c>HttpContext.User</c>, and <see cref="ClaimsCurrentActor"/> (here, in the
/// Application layer) is the only place that turns those primitives into a domain type. That is what
/// lets the architecture test assert that <c>AjBoilerplate.Api</c> does not reference
/// <c>AjBoilerplate.Domain</c> at all.
/// </summary>
public interface IActorClaims
{
    string Id { get; }

    string Name { get; }

    /// <summary>The caller's single effective, canonical role — see
    /// <see cref="ApplicationRoles.Effective"/>. Empty when the caller holds no recognised role.</summary>
    string Role { get; }

    string Email { get; }

    /// <summary>Every canonical role the caller holds. Never raw authorization-server keys.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Optional group/tenant/scope membership from the token, for ownership checks. Empty
    /// when the token carries no such claim.</summary>
    IReadOnlyList<string> Groups { get; }
}
