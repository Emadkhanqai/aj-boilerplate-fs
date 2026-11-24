using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using AjBoilerplate.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// After the caller authenticates, this projects every role shape a realm may emit into standard
/// <see cref="ClaimTypes.Role"/> claims carrying the canonical role names, so ASP.NET authorization
/// policies see one role source regardless of how the realm is configured. Four sources are merged —
/// a token may legitimately carry more than one:
/// <list type="bullet">
///   <item><c>realm_access</c> — one claim whose value is a JSON object with a <c>roles</c> array.
///     Read ONLY when <see cref="KeycloakOptions.HonourRealmRoles"/> is true: a shared realm's
///     realm-level <c>admin</c> belongs to whichever application granted it, and must not become this
///     portal's administrative tier.</item>
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
///
/// <para>
/// <b>It projects a SECOND vocabulary too, and keeps the two apart.</b> A Keycloak client carries
/// <c>resource:action</c> permission roles alongside the seat, both arriving in the same flat
/// <c>roles</c> claim. Seats land on <see cref="ClaimTypes.Role"/>; permissions land on
/// <see cref="PermissionClaims.ClaimType"/> and never on <see cref="ClaimTypes.Role"/>, so everything
/// that decides who the caller IS sees exactly the seats. Values in neither vocabulary are dropped.
/// </para>
/// </summary>
public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    private const string RealmAccessClaim = "realm_access";
    private const string ResourceAccessClaim = "resource_access";
    private const string FlatRolesClaim = "roles";

    /// <summary>
    /// Role keys already reported by <see cref="WarnAboutUnrecognisedKeys"/>, so a steady stream of
    /// requests carrying the same unmapped key logs once per process instead of once per request.
    /// Static on purpose, and bounded by <see cref="WarnedKeyLimit"/>: a set that only ever grows is a
    /// leak waiting for a misconfigured realm, and the value is in the first few keys anyway.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> WarnedUnrecognisedKeys = new(StringComparer.Ordinal);

    private const int WarnedKeyLimit = 64;

    private const int MaxLoggedKeyLength = 120;

    private readonly IOptions<KeycloakOptions> _options;
    private readonly ILogger<KeycloakRoleClaimsTransformation> _logger;

    public KeycloakRoleClaimsTransformation(
        IOptions<KeycloakOptions> options, ILogger<KeycloakRoleClaimsTransformation> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        // Both vocabularies are derived from ONE read of the raw keys, before anything is added to the
        // identity. The collection this reads is the one the loops below append to, so re-reading it
        // between them would decide the second vocabulary from a principal the first loop had mutated.
        var rawKeys = RawRoleKeys(principal, _options.Value.ClientId, _options.Value.HonourRealmRoles);

        WarnAboutUnrecognisedKeys(rawKeys, _logger);

        foreach (var role in ApplicationRolesFrom(rawKeys).Where(role => !principal.IsInRole(role)))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        // Permissions go onto their OWN claim type, never ClaimTypes.Role — see PermissionClaims.
        foreach (var permission in PermissionsFrom(rawKeys)
                     .Where(permission => !principal.HasClaim(PermissionClaims.ClaimType, permission)))
        {
            identity.AddClaim(new Claim(PermissionClaims.ClaimType, permission));
        }

        return Task.FromResult(principal);
    }

    /// <summary>
    /// Every seat on <paramref name="principal"/> — <paramref name="clientId"/>'s client roles, a flat
    /// <c>roles</c> claim, anything already on <see cref="ClaimTypes.Role"/>, and realm roles when
    /// <paramref name="honourRealmRoles"/> — translated to canonical role names, de-duplicated, with
    /// unmapped keys dropped.
    ///
    /// <para>
    /// Exposed as a standalone step so an authentication event can resolve the caller's role at
    /// token-validation time, before <see cref="IClaimsTransformation"/> has run. That is exactly where
    /// the claim-mapping hazard above bites, and <see cref="ExtractRoleTypeClaims"/> is what makes this
    /// safe to call there.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ExtractApplicationRoles(
        ClaimsPrincipal principal, string? clientId, bool honourRealmRoles = false) =>
        ApplicationRolesFrom(RawRoleKeys(principal, clientId, honourRealmRoles));

    /// <summary>
    /// Every <see cref="Permissions"/> value on <paramref name="principal"/>, canonicalised and
    /// de-duplicated, with unrecognised strings dropped. Permissions arrive in the SAME sources as
    /// seats, so this reads exactly what <see cref="ExtractApplicationRoles"/> reads and differs only
    /// in which table it resolves against. The two results are disjoint by construction.
    /// </summary>
    public static IReadOnlyList<string> ExtractPermissions(
        ClaimsPrincipal principal, string? clientId, bool honourRealmRoles = false) =>
        PermissionsFrom(RawRoleKeys(principal, clientId, honourRealmRoles));

    // Materialising here is load-bearing, not a style choice: TransformAsync appends to the very claim
    // collection ExtractRoleTypeClaims enumerates, so a lazy sequence would leave the foreach mutating
    // what it is iterating — InvalidOperationException on every authenticated request.
    private static List<string> RawRoleKeys(ClaimsPrincipal principal, string? clientId, bool honourRealmRoles) =>
        (honourRealmRoles ? ExtractRealmRoles(principal) : [])
            .Concat(ExtractClientRoles(principal, clientId))
            .Concat(ExtractFlatRoles(principal))
            .Concat(ExtractRoleTypeClaims(principal))
            .ToList();

    private static IReadOnlyList<string> ApplicationRolesFrom(List<string> rawKeys) =>
        rawKeys
            .Select(KeycloakRoles.ToApplicationRole)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> PermissionsFrom(List<string> rawKeys) =>
        rawKeys
            .Select(Permissions.Canonical)
            .Where(permission => permission is not null)
            .Select(permission => permission!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Logs a warning, once per distinct key per process, for every key that is neither a seat nor a
    /// permission and was therefore silently dropped. Every source here fails closed silently, which
    /// is how a real seat with an unmapped key once resolved to "no role" with nothing in the log.
    /// Only the rejected key is logged — never the token, the subject or any other claim — scrubbed of
    /// control characters and truncated, because it is externally supplied text. A recognised
    /// permission is not an unrecognised key and is not reported.
    /// </summary>
    private static void WarnAboutUnrecognisedKeys(List<string> rawKeys, ILogger logger)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        foreach (var key in rawKeys.Where(key =>
                     KeycloakRoles.ToApplicationRole(key) is null && !Permissions.IsPermission(key)))
        {
            if (WarnedUnrecognisedKeys.Count >= WarnedKeyLimit || !WarnedUnrecognisedKeys.TryAdd(key, 0))
            {
                continue;
            }

            logger.LogWarning(
                "Role key '{RoleKey}' from the authorization server is not a recognised seat or permission and was dropped. "
                + "If it is a real one, add it to ApplicationRoles.Canonical or Permissions.All; this is logged once per key per process.",
                Scrub(key));
        }
    }

    private static string Scrub(string key)
    {
        var cleaned = string.Concat(key.Select(c => char.IsControl(c) ? ' ' : c));
        return cleaned.Length <= MaxLoggedKeyLength ? cleaned : cleaned[..MaxLoggedKeyLength] + "…";
    }

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
