using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Resolves the identity performing the current use case. Backed by
/// <see cref="Identity.ClaimsCurrentActor"/> once <c>AddApiAuthentication</c> registers the claims
/// source; the <see cref="Identity.SystemCurrentActor"/> registration in <c>AddApplication</c> is
/// only a placeholder default for contexts that never wire up authentication.
/// </summary>
public interface ICurrentActor
{
    Actor GetActor();
}
