using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// After the caller authenticates, this projects every role shape a realm may emit into standard
/// <see cref="ClaimTypes.Role"/> claims carrying the canonical role names, so ASP.NET authorization
/// policies see one role source regardless of how the realm is configured. Four sources are merged —
/// a token may legitimately carry more than one:
/// <list type="bullet">
///   <item><c>realm_access</c> — one claim whose value is a JSON object with a <c>roles</c> array.</item>
///   <item><c>resource_access</c> — the same, nested under the configured client id.</item>
///   <item>A literal <c>roles</c> claim, for a realm that emits the flat shape AND a pipeline that
///     leaves the name alone (see the hazard below — the default pipeline does not).</item>
///   <item><see cref="ClaimTypes.Role"/> itself, which is where a flat <c>roles</c> claim actually
///     arrives under the default configuration.</item>
/// </list>
///
/// <para>
/// <b>Hazard — read before changing how roles are read off a principal.</b>
/// <c>JwtBearerHandler</c> applies .NET's default INBOUND CLAIM-TYPE MAPPING before
/// <c>OnTokenValidated</c> and before any <see cref="IClaimsTransformation"/> runs, and that mapping
/// RENAMES a token's flat top-level <c>roles</c> claim to <see cref="ClaimTypes.Role"/>. Against
/// such a realm no claim literally named <c>roles</c> exists on the principal at all by the time
/// this class sees it. Code that looks only for the literal name therefore resolves nothing, and
/// does so silently. Always read <see cref="ClaimTypes.Role"/> as well — that is what
/// <see cref="ExtractRoleTypeClaims"/> is for, and why removing it would reopen a real production
/// outage. (Setting <c>MapInboundClaims = false</c> would disable the renaming, but the API does
/// not, and this class must not depend on that.)
/// </para>
///
/// <para>
/// Idempotent: it runs on every request — and may run more than once within one request — and never
/// adds a role twice. That property is load-bearing now that <see cref="ClaimTypes.Role"/> is both
/// an input and the output: canonicalisation collapses the raw key and the display name onto one
/// value, <c>Distinct</c> runs after canonicalisation, and the
/// <see cref="ClaimsPrincipal.IsInRole"/> filter in <see cref="TransformAsync"/> drops anything
/// already present, so the principal cannot grow per request.
/// </para>
/// </summary>
public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    private const string RealmAccessClaim = "realm_access";
    private const string ResourceAccessClaim = "resource_access";
    private const string FlatRolesClaim = "roles";

    private readonly IOptions<KeycloakOptions> _options;

    public KeycloakRoleClaimsTransformation(IOptions<KeycloakOptions> options) => _options = options;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var clientId = _options.Value.ClientId;
        foreach (var role in ExtractApplicationRoles(principal, clientId).Where(role => !principal.IsInRole(role)))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return Task.FromResult(principal);
    }

    /// <summary>
    /// Every role on <paramref name="principal"/> — realm roles, <paramref name="clientId"/>'s client
    /// roles, a flat <c>roles</c> claim, and anything already sitting on
    /// <see cref="ClaimTypes.Role"/> — translated to canonical role names, de-duplicated, with
    /// unmapped keys dropped.
    ///
    /// <para>
    /// Exposed as a standalone step — not only reachable through <see cref="TransformAsync"/> — so an
    /// authentication event can resolve the caller's role at token-validation time, before
    /// <see cref="IClaimsTransformation"/> has run for that request (transformations run after the
    /// handler's events, inside <c>AuthenticationService.AuthenticateAsync</c>). That call site is
    /// exactly where the claim-mapping hazard described on this class bites, because the handler has
    /// already renamed a flat <c>roles</c> claim by then; <see cref="ExtractRoleTypeClaims"/> is what
    /// makes this method safe to use there.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ExtractApplicationRoles(ClaimsPrincipal principal, string? clientId) =>
        ExtractRealmRoles(principal)
            .Concat(ExtractClientRoles(principal, clientId))
            .Concat(ExtractFlatRoles(principal))
            .Concat(ExtractRoleTypeClaims(principal))
            .Select(KeycloakRoles.ToApplicationRole)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
            // Materialising here is load-bearing, not a style choice. TransformAsync appends to the
            // very claim collection ExtractRoleTypeClaims enumerates, so returning a lazy sequence
            // would leave the foreach mutating what it is iterating — InvalidOperationException on
            // every authenticated request. Do not relax this to IEnumerable<string>.
            .ToList();

    private static List<string> ExtractRealmRoles(ClaimsPrincipal principal)
    {
        var realmAccess = principal.FindFirst(RealmAccessClaim)?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array)
            {
                return roles.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // A malformed realm_access claim grants no roles rather than failing the request.
        }

        return [];
    }

    // The resource_access claim carries a per-client roles list; only the entry belonging to our own
    // configured client grants roles, since other applications in a shared realm may reuse the same
    // role keys for entirely different meanings.
    private static List<string> ExtractClientRoles(ClaimsPrincipal principal, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return [];
        }

        var resourceAccess = principal.FindFirst(ResourceAccessClaim)?.Value;
        if (string.IsNullOrWhiteSpace(resourceAccess))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(resourceAccess);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(clientId, out var client)
                && client.ValueKind == JsonValueKind.Object
                && client.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array)
            {
                return roles.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Malformed resource_access grants no roles rather than failing the request.
        }

        return [];
    }

    // A realm can also be configured to emit roles as a flat top-level "roles" claim (commonly done
    // to keep tokens small). JsonWebTokenHandler — the .NET 8+ JwtBearer default — expands a
    // top-level JSON array into MULTIPLE claims all named "roles", each holding one plain string,
    // unlike realm_access/resource_access which arrive as a single claim whose value is a JSON
    // object string. Handling only that multi-claim shape would silently do nothing against a
    // configuration that instead delivers one "roles" claim whose value is a JSON array string, so
    // every value is checked for that shape first and only treated as a literal role name when it is
    // not one. A value that fails to parse fails closed the same way every other unmapped key does.
    //
    // THIS SOURCE ALONE IS NOT ENOUGH, and on the default configuration it matches nothing at all:
    // JwtBearer's inbound claim-type mapping has already renamed "roles" to ClaimTypes.Role before
    // this runs. ExtractRoleTypeClaims below is what actually catches the flat shape. This one is
    // kept for a pipeline with MapInboundClaims turned off, where the original name does survive.
    private static List<string> ExtractFlatRoles(ClaimsPrincipal principal) =>
        principal.FindAll(FlatRolesClaim)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(ExpandFlatRoleValue)
            .ToList();

    private static IEnumerable<string> ExpandFlatRoleValue(string value) =>
        TryParseJsonStringArray(value, out var arrayRoles) ? arrayRoles : [value];

    private static bool TryParseJsonStringArray(string value, out List<string> roles)
    {
        roles = [];

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            roles = document.RootElement.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Roles already sitting on <see cref="ClaimTypes.Role"/>. For a realm emitting the flat shape
    /// this is the ONLY source that matches, because JwtBearer's default inbound claim-type mapping
    /// renamed <c>roles</c> to this type before any of this code ran — see the hazard on the class.
    /// It is also the claim type this transformation writes to, so values arrive here both as the
    /// authorization server's raw key and, on a second pass, as the canonical display name;
    /// <c>Canonical</c> maps a display name to itself and <c>Distinct</c> runs afterwards, so
    /// re-reading our own output cannot add anything.
    /// </summary>
    private static List<string> ExtractRoleTypeClaims(ClaimsPrincipal principal) =>
        principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
}
