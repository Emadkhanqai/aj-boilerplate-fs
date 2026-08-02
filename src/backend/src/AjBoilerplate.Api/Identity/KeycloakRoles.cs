using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Maps Keycloak realm/client role keys to the application's canonical role names. Keycloak is the
/// source of truth for WHO holds a role; the API only translates its lowercase keys to the names the
/// application uses. Unknown keys map to null — a role is never granted implicitly.
/// </summary>
/// <remarks>
/// The mapping itself lives in <see cref="ApplicationRoles"/> (Application layer) so the
/// Application-layer capability rules canonicalise a role with the SAME table this translation uses.
/// Two copies of the table would be free to drift, and drift here fails silently: policies keep
/// working while every list a user sees quietly comes back empty.
/// </remarks>
public static class KeycloakRoles
{
    public static string? ToApplicationRole(string realmRole) => ApplicationRoles.Canonical(realmRole);
}
