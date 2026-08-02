using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Keycloak-scheme JwtBearer lifecycle hook. A shared realm issues tokens to more than one
/// application, so a token can carry a valid issuer AND a valid signature and still have been issued
/// to, or exchanged for, a different client entirely — accepting it would let another application's
/// users straight in. This runs an <c>azp</c> (authorized party) check before the principal is
/// trusted for anything.
///
/// This is also the natural place to add "establish a backend session for this token" behaviour —
/// upserting a user row, recording a sign-in event — if a consuming project needs one.
/// </summary>
public static class KeycloakAuthenticationEvents
{
    public static Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var clientId = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<KeycloakOptions>>().Value.ClientId;
        var azp = context.Principal?.FindFirst("azp")?.Value;

        if (!IsAllowedAzp(azp, clientId))
        {
            // azp names a client, not a secret — safe to log as-is.
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(KeycloakAuthenticationEvents).FullName!)
                .LogWarning("Rejected a token issued for a different client (azp={Azp}).", azp ?? "(missing)");
            context.Fail("Token was not issued for this API's client.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pure azp-vs-configured-client decision, factored out of <see cref="OnTokenValidatedAsync"/> so
    /// it can be unit-tested without constructing a full <see cref="TokenValidatedContext"/>.
    /// Case-sensitive exact match: azp is a client identifier, never something to fuzzy-match. A
    /// blank configured client id rejects everything rather than accepting everything — the check
    /// fails closed.
    /// </summary>
    public static bool IsAllowedAzp(string? azp, string expectedClientId) =>
        !string.IsNullOrEmpty(azp)
        && !string.IsNullOrEmpty(expectedClientId)
        && string.Equals(azp, expectedClientId, StringComparison.Ordinal);
}
