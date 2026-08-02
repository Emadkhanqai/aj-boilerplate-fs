using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// After the caller authenticates, this projects Keycloak's <c>realm_access.roles</c>, the configured
/// client's <c>resource_access.&lt;clientId&gt;.roles</c>, and a flat top-level <c>roles</c> claim
/// into standard <see cref="ClaimTypes.Role"/> claims carrying the canonical role names, so ASP.NET
/// authorization policies see one role source regardless of how the realm is configured. All three
/// shapes are merged — a token may legitimately carry more than one. Idempotent: it runs on every
/// request and never adds a role twice.
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
    /// The realm roles plus <paramref name="clientId"/>'s client roles on
    /// <paramref name="principal"/>, translated to canonical role names (unmapped keys dropped).
    /// Exposed as a standalone step — not only reachable through <see cref="TransformAsync"/> — so an
    /// authentication event can resolve the caller's role at token-validation time, before
    /// <see cref="IClaimsTransformation"/> has run for that request (transformations run after the
    /// handler's events, inside <c>AuthenticationService.AuthenticateAsync</c>).
    /// </summary>
    public static IReadOnlyList<string> ExtractApplicationRoles(ClaimsPrincipal principal, string? clientId) =>
        ExtractRealmRoles(principal)
            .Concat(ExtractClientRoles(principal, clientId))
            .Concat(ExtractFlatRoles(principal))
            .Select(KeycloakRoles.ToApplicationRole)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
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
}
