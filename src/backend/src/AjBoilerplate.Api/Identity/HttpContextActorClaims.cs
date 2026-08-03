using System.Security.Claims;
using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Reads the authenticated caller's identity from the current HTTP principal. Exposes only primitive
/// strings so the Api layer never constructs a domain <c>Actor</c> — that happens in
/// <c>ClaimsCurrentActor</c>, which is what lets the architecture test assert this project has no
/// reference to the Domain assembly at all.
/// </summary>
public sealed class HttpContextActorClaims : IActorClaims
{
    private readonly IHttpContextAccessor _accessor;
    private IReadOnlyList<string>? _roles;
    private IReadOnlyList<string>? _groups;

    public HttpContextActorClaims(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    public string Id =>
        User?.FindFirst("oid")?.Value
            ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value
            // The shared constant, not a literal: a use case that records something per user must be
            // able to recognise "there is no real subject here" using the same value produced here.
            ?? ActorIdentifiers.Anonymous;

    public string Name =>
        User?.FindFirst("name")?.Value
            ?? User?.FindFirst("preferred_username")?.Value
            ?? User?.Identity?.Name
            ?? "Anonymous";

    // Resolved over ALL role claims — never FindFirst. A real Keycloak token yields two
    // ClaimTypes.Role claims for one seat: the raw key ("editor"), projected by the JwtBearer
    // handler straight from the token, and the canonical display name ("Editor") appended afterwards
    // by KeycloakRoleClaimsTransformation. FindFirst returns whichever came first — in practice the
    // RAW key, which matches none of the capability tables, so every caller silently collapses to
    // "no role" and sees an empty application. Effective() canonicalises and picks the caller's
    // highest-authority seat, so this can never disagree with what the policies enforce.
    public string Role => ApplicationRoles.Effective(Roles) ?? string.Empty;

    public string Email =>
        User?.FindFirst("email")?.Value
            ?? User?.FindFirst(ClaimTypes.Email)?.Value
            ?? User?.FindFirst("preferred_username")?.Value
            ?? string.Empty;

    // Materialised once per scoped instance (one request), so the getter never re-projects on each
    // read.
    //
    // CANONICALISED, never raw — for the reason described on Role above. Every server-side consumer
    // canonicalises defensively, so a raw key leaking through is invisible on the server; but this
    // list is also what a "who am I" endpoint would project to the client, which only recognises the
    // canonical names. Canonicalising at this single boundary keeps server behaviour identical and
    // gives the client the only shape it can read. Unmapped keys are dropped rather than passed
    // through, so a role is still never granted — or displayed — implicitly.
    public IReadOnlyList<string> Roles =>
        _roles ??= User?.FindAll(ClaimTypes.Role)
            .Select(c => ApplicationRoles.Canonical(c.Value))
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    /// <summary>The group(s) the caller belongs to, from a Keycloak-mapped <c>groups</c> claim (may
    /// be multi-valued). Empty when the token carries no such claim.</summary>
    public IReadOnlyList<string> Groups =>
        _groups ??= User?.FindAll("groups")
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}
