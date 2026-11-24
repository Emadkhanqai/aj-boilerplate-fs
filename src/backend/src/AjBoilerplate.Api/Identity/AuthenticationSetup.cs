using System.Security.Claims;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Wires bearer-token authentication and authorization for the gateway topology: the API only ever
/// sees Keycloak JWTs, whichever cloud it runs on. See <c>docs/auth/gateway-contract.md</c>.
///
/// SCHEME SELECTION, in order:
/// <list type="number">
///   <item><b>Keycloak</b> when the <c>Keycloak</c> section is configured. Signing keys come from
///     <c>Keycloak:JwksUri</c> through <see cref="KeycloakSigningKeyProvider"/> and are pre-warmed at
///     startup. Outside Development a plaintext JWKS URI, or no JWKS URI without
///     <see cref="KeycloakOptions.AllowDiscoveryFallback"/>, refuses to start.
///     <see cref="KeycloakAuthenticationEvents"/> rejects a token whose <c>azp</c> names a different
///     client.</item>
///   <item><b>Nothing configured</b> (local, offline, CI) — a bare JWT scheme with no signing
///     authority, so protected endpoints correctly answer 401 rather than failing to start or, far
///     worse, silently accepting anything.</item>
/// </list>
///
/// There is no "cloud IdP directly" scheme: identity is Entra ID via the Auth service on every cloud,
/// exchanged at Keycloak, so a second token shape would only be a second way in.
///
/// An integration-test scheme, if any, is registered by the test host and is unaffected by all of this.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>
    /// Bounds the OIDC metadata fetch when discovery is in use. The framework default is 60 s, tried
    /// twice: an unreachable host or a scheme that does not answer then looks like a slow API for two
    /// minutes and ends in a 401 that names nothing. Matches the 8 s timeout
    /// <see cref="KeycloakSigningKeyProvider"/>'s own client uses, plus headroom.
    /// </summary>
    private static readonly TimeSpan MetadataBackchannelTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
        services.Configure<AuthModeOptions>(configuration.GetSection(AuthModeOptions.SectionName));

        var mode = configuration.GetSection(AuthModeOptions.SectionName).Get<AuthModeOptions>() ?? new AuthModeOptions();
        if (!mode.IsGateway)
        {
            // An unknown mode must not be read as "something weaker": there is only one mode.
            throw new InvalidOperationException(
                $"'{AuthModeOptions.SectionName}:{nameof(AuthModeOptions.Mode)}' is '{mode.Mode}', which is not a recognised mode. "
                + $"The only mode is '{AuthModeOptions.Gateway}' (also the default when blank).");
        }

        var authBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        var keycloak = configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>() ?? new KeycloakOptions();

        if (keycloak.IsConfigured)
        {
            AddKeycloakScheme(services, authBuilder, keycloak, environment.IsDevelopment());
        }
        else
        {
            // Nothing configured: no bearer token can validate, so [Authorize] endpoints 401.
            authBuilder.AddJwtBearer();
        }

        // The Auth service's session-ended events: two additional, opt-in-per-endpoint schemes (a shared key,
        // or a Google ID token). Nothing but that one controller names them, so they never touch Keycloak's.
        authBuilder
            .AddScheme<AuthenticationSchemeOptions, AuthBackendEventsKeyHandler>(AuthBackendAuthenticationDefaults.KeySchemeName, _ => { })
            .AddAuthBackendGoogleIdToken(configuration);

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

        // SignInRecordingMiddleware's per-session "already recorded" memo.
        services.AddMemoryCache();

        return services;
    }

    /// <summary>
    /// Fills the Keycloak JWKS cache before the first request, so an unreachable identity endpoint
    /// announces itself as a startup warning rather than as a stranger's failed sign-in. A no-op when
    /// Keycloak or <c>Keycloak:JwksUri</c> is unset, and never fatal.
    /// </summary>
    public static async Task PreWarmKeycloakSigningKeysAsync(this WebApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<KeycloakOptions>>().Value.JwksUri))
        {
            return;
        }

        if (app.Services.GetService<KeycloakSigningKeyProvider>() is { } provider)
        {
            await provider.WarmUpAsync();
        }
    }

    private static void AddKeycloakScheme(
        IServiceCollection services, AuthenticationBuilder authBuilder, KeycloakOptions keycloak, bool isDevelopment)
    {
        if (keycloak.RequiresExplicitJwksUri(isDevelopment))
        {
            // Intent stated and not honourable: the operator asked for Keycloak authentication and
            // supplied no way to obtain signing keys that this environment is known to reach. Booting
            // anyway does not degrade gracefully — requests stall on whatever jwks_uri discovery
            // advertises and then answer a 401 that reads as a bad token, sending the diagnosis into
            // the wrong layer for hours.
            throw new InvalidOperationException(
                $"Keycloak is configured but '{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.JwksUri)}' is empty, and this is not the Development environment. "
                + "Set it to the realm's https JWKS endpoint (.../protocol/openid-connect/certs). "
                + $"If '{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.Authority)}' really does serve /.well-known/openid-configuration from this network "
                + $"AND advertises a jwks_uri that answers, say so explicitly by setting '{KeycloakOptions.AllowDiscoveryFallbackKey}' to true.");
        }

        if (keycloak.RefusesInsecureJwksUri(isDevelopment))
        {
            // A plaintext JWKS can be substituted on the network path, and a substituted key means
            // forged tokens accepted as genuine, for any role.
            throw new InvalidOperationException(
                $"'{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.JwksUri)}' is not an https URL, and this is not the Development environment. "
                + "Signing keys fetched in plaintext can be substituted by anyone on the network path, which would let forged tokens through. "
                + $"Use the realm's https JWKS endpoint. If this environment has a dated, written acceptance of plaintext transport, set '{KeycloakOptions.AllowInsecureJwksTransportKey}' to true.");
        }

        authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
            options.BackchannelTimeout = MetadataBackchannelTimeout;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = keycloak.ValidIssuer,

                // Audience is deliberately not validated here: a token obtained by exchange carries
                // azp=<this client> rather than a stable aud. KeycloakAuthenticationEvents enforces
                // azp instead — an equivalent, and for this flow more accurate, check.
                ValidateAudience = false,

                // The default 5-minute tolerance lets JwtBearer accept a token past its own `exp`.
                // These tokens are minted for this portal by a server we trust the clock of, and
                // accepting an expired one is exactly the "small" leniency that becomes a real
                // session-lifetime bug.
                ClockSkew = TimeSpan.Zero,
            };

            if (string.IsNullOrWhiteSpace(keycloak.JwksUri))
            {
                // No explicit JWKS endpoint, and this environment has opted into discovery (or is
                // Development) — the guards above have rejected every other way of reaching here.
                options.MetadataAddress = $"{keycloak.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            }

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = KeycloakAuthenticationEvents.OnTokenValidatedAsync,
            };
        });

        // Signing keys come from Keycloak:JwksUri via KeycloakSigningKeyProvider rather than
        // discovery. Wired as a named-options post-configure step (not inline above) because it needs
        // the DI-resolved provider singleton, and the delegate passed to AddJwtBearer has no access to
        // IServiceProvider.
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

    /// <summary>
    /// Policies resolve through <see cref="RoleCapabilities"/> rather than naming roles inline, so the
    /// server's enforcement and the capability flags <c>/api/v1/me</c> hands the client share one
    /// source of truth and cannot diverge. Each is satisfied by the seat hierarchy OR the matching
    /// permission: read by viewer, editor, admin or <see cref="Permissions.ItemsRead"/>; write by
    /// editor, admin or <see cref="Permissions.ItemsWrite"/>; administer by admin or
    /// <see cref="Permissions.AdminManage"/>.
    /// </summary>
    private static void AddAuthorizationPolicies(IServiceCollection services) =>
        services.AddAuthorizationBuilder()
            // THE FALLBACK POLICY — deny by default. It applies to every endpoint that declares NO
            // authorization metadata of its own. ControllerConventionTests catches a controller that
            // forgot to declare a posture, at build time, but cannot see a minimal-API endpoint, a
            // health check or anything else registered outside a controller; this catches those at
            // runtime, by denying.
            //
            // THE HEALTH ENDPOINTS MUST BE EXEMPTED (Program.cs, .AllowAnonymous()) WHENEVER THIS LINE
            // EXISTS. Without that, every platform startup, liveness and readiness probe answers 401,
            // every instance is marked unhealthy, and the service goes down.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.ReadAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => CapabilitiesOf(ctx.User).CanRead))
            .AddPolicy(Policies.WriteAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => CapabilitiesOf(ctx.User).CanWrite))
            .AddPolicy(Policies.AdminAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => CapabilitiesOf(ctx.User).CanAdminister))
            // The identity's authentication TYPE is checked too: a person's token can carry any "name", but
            // only these two schemes stamp their own scheme name as the type.
            .AddPolicy(Policies.AuthBackendServiceAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.Identity is { Name: AuthBackendAuthenticationDefaults.ServiceIdentityName, AuthenticationType: AuthBackendAuthenticationDefaults.KeySchemeName or AuthBackendAuthenticationDefaults.GoogleSchemeName }));

    /// <summary>The three coarse capabilities for <paramref name="user"/>: seat unioned with permissions.
    /// The one function both the policies and <c>/api/v1/me</c> resolve through.</summary>
    private static RoleCapabilities CapabilitiesOf(ClaimsPrincipal user) =>
        RoleCapabilities.For(
            user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
            user.FindAll(PermissionClaims.ClaimType).Select(c => c.Value).ToList());
}
