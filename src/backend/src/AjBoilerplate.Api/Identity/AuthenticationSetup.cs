using System.Security.Claims;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Infrastructure.Cloud;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Wires bearer-token authentication and role-based authorization.
///
/// SCHEME SELECTION, in order:
/// <list type="number">
///   <item><b>Keycloak</b> when the <c>Keycloak</c> section is configured. This is the recommended
///     topology: Keycloak federates to the cloud IdP and issues the tokens this API validates, so
///     authorization is identical on every cloud. Signing keys come from <c>Keycloak:JwksUri</c>
///     directly via <see cref="KeycloakSigningKeyProvider"/> (falling back to OIDC discovery only
///     when that is blank), and <see cref="KeycloakAuthenticationEvents"/> rejects a token whose
///     <c>azp</c> names a different client.</item>
///   <item><b>The cloud IdP directly</b> — Google Cloud Identity for <c>CLOUD_PROVIDER=gcp</c>,
///     Microsoft Entra ID for <c>azure</c>. THIS IS THE ONLY PLACE AUTHENTICATION BRANCHES ON THE
///     CLOUD. Both are ordinary OIDC issuers, so the branch selects a configuration section and
///     nothing more.</item>
///   <item><b>Nothing configured</b> (local, offline, CI) — a bare JWT scheme with no signing
///     authority, so protected endpoints correctly answer 401 rather than failing to start or,
///     far worse, silently accepting anything.</item>
/// </list>
///
/// An integration-test scheme, if any, is registered by the test host and is unaffected by all of
/// this.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
        services.Configure<UpstreamIdentityOptions>(configuration.GetSection(UpstreamIdentityOptions.SectionName));

        var keycloak = configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>() ?? new KeycloakOptions();
        var upstream = configuration.GetSection(UpstreamIdentityOptions.SectionName).Get<UpstreamIdentityOptions>() ?? new UpstreamIdentityOptions();
        var cloud = configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();

        if (keycloak.IsConfigured)
        {
            AddKeycloakScheme(services, authBuilder, keycloak);
        }
        else
        {
            var provider = cloud.Resolve();
            var idp = provider == CloudProvider.Azure ? upstream.EntraId : upstream.GoogleCloudIdentity;

            if (idp.IsConfigured)
            {
                AddUpstreamIdpScheme(authBuilder, idp);
            }
            else
            {
                // No IdP configured: no bearer token can validate, so [Authorize] endpoints 401.
                authBuilder.AddJwtBearer();
            }
        }

        services.AddSingleton<IClaimsTransformation, KeycloakRoleClaimsTransformation>();

        AddAuthorizationPolicies(services);

        // The current actor comes from the authenticated principal. HttpContextActorClaims exposes
        // primitives only; ClaimsCurrentActor (Application layer) is what turns them into a domain
        // Actor, so this project never touches the Domain assembly.
        services.AddHttpContextAccessor();
        services.AddScoped<IActorClaims, HttpContextActorClaims>();
        services.AddScoped<ICurrentActor, ClaimsCurrentActor>();

        // Ties an audit entry or an outbox row back to the HTTP request that produced it.
        services.AddScoped<ICorrelationContext, HttpCorrelationContext>();

        return services;
    }

    private static void AddKeycloakScheme(IServiceCollection services, AuthenticationBuilder authBuilder, KeycloakOptions keycloak)
    {
        authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = keycloak.ValidIssuer,

                // Audience is deliberately not validated here: a token obtained by exchange carries
                // azp=<this client> rather than a stable aud. KeycloakAuthenticationEvents enforces
                // azp instead — an equivalent, and for this flow more accurate, check.
                ValidateAudience = false,

                // The default 5-minute tolerance lets JwtBearer accept a token past its own `exp`.
                // These are machine-to-machine tokens with no interactive browser clock to
                // accommodate, so no skew is warranted, and accepting an expired token is exactly
                // the kind of "small" leniency that turns into a real session-lifetime bug.
                ClockSkew = TimeSpan.Zero,
            };

            if (string.IsNullOrWhiteSpace(keycloak.JwksUri))
            {
                // No explicit JWKS endpoint for this environment — fall back to discovery. See
                // KeycloakSigningKeyProvider for why JwksUri is preferred whenever it is set.
                options.MetadataAddress = $"{keycloak.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            }

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = KeycloakAuthenticationEvents.OnTokenValidatedAsync,
            };
        });

        // Signing keys come from Keycloak:JwksUri via KeycloakSigningKeyProvider rather than
        // discovery. Wired as a named-options post-configure step (not inline above) because it
        // needs the DI-resolved provider singleton, and the delegate passed to AddJwtBearer has no
        // access to IServiceProvider.
        services.AddHttpClient(nameof(KeycloakSigningKeyProvider), c => c.Timeout = TimeSpan.FromSeconds(8));
        services.AddSingleton<KeycloakSigningKeyProvider>();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<KeycloakSigningKeyProvider>((options, provider) =>
            {
                if (!string.IsNullOrWhiteSpace(keycloak.JwksUri))
                {
                    options.TokenValidationParameters.IssuerSigningKeyResolver =
                        (_, _, kid, _) => provider.GetSigningKeys(kid);
                }
            });
    }

    private static void AddUpstreamIdpScheme(AuthenticationBuilder authBuilder, IdentityProviderOptions idp)
    {
        authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = idp.Authority;
            options.Audience = idp.Audience;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                // Validate the audience whenever one is configured. Leaving it off in a multi-tenant
                // cloud directory means any token that directory issued — including one minted for a
                // completely different application — validates here.
                ValidateAudience = !string.IsNullOrWhiteSpace(idp.Audience),
                ClockSkew = TimeSpan.Zero,
            };
        });
    }

    /// <summary>
    /// Policies resolve through <see cref="RoleCapabilities"/> rather than naming roles inline, so
    /// the server's enforcement and any capability flags handed to a client share one source of
    /// truth and cannot diverge.
    /// </summary>
    private static void AddAuthorizationPolicies(IServiceCollection services) =>
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.ReadAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => RoleCapabilities.For(RolesOf(ctx.User)).CanRead))
            .AddPolicy(Policies.WriteAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => RoleCapabilities.For(RolesOf(ctx.User)).CanWrite))
            .AddPolicy(Policies.AdminAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => RoleCapabilities.For(RolesOf(ctx.User)).CanAdminister));

    private static List<string> RolesOf(ClaimsPrincipal user) =>
        user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
}
