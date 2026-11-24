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
    private IReadOnlyList<string>? _permissions;

    public HttpContextActorClaims(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    public string Id =>
        ActorIdClaim.Find(User)
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

    /// <summary>Every canonical permission the caller holds, from <see cref="PermissionClaims.ClaimType"/>
    /// — never from <see cref="ClaimTypes.Role"/>, so a permission can never be read as a seat.</summary>
    public IReadOnlyList<string> Permissions =>
        _permissions ??= User?.FindAll(PermissionClaims.ClaimType)
            .Select(c => AjBoilerplate.Application.Identity.Permissions.Canonical(c.Value))
            .Where(permission => permission is not null)
            .Select(permission => permission!)
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

/// <summary>
/// The one definition of "which claim names this person", shared by the actor, the rate limiter and the
/// sign-in recording so they cannot name one person three ways. <c>oid</c>, then the mapped <c>sub</c>
/// (inbound claim mapping renames a Keycloak <c>sub</c> to <see cref="ClaimTypes.NameIdentifier"/>), then
/// the literal <c>sub</c>.
/// </summary>
public static class ActorIdClaim
{
    public static string? Find(ClaimsPrincipal? user) =>
        user?.FindFirst("oid")?.Value
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("sub")?.Value;
}
