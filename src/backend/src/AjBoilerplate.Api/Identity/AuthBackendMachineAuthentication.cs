using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Settings for the Auth service's session-ended events (<c>docs/auth/session-ended-contract.md</c>). The
/// receiver is a machine surface with TWO ways in, either of which is enough:
/// <list type="bullet">
/// <item><see cref="EventsKey"/> (the default): a shared secret the sender puts in <c>X-Auth-Events-Key</c>.
/// From the secrets provider; never a file, never logged.</item>
/// <item>A Google ID token (opt-in, as USP's CAD does it): <see cref="Audience"/> this service's own origin(s)
/// AND <see cref="AllowedCallerEmail"/> the one service account allowed to call.</item>
/// </list>
/// With neither configured the endpoint does not exist (404); with the key set and a wrong key it is 401.
/// </summary>
public sealed class AuthBackendIntegrationOptions
{
    public const string SectionName = "Integration:AuthBackend";

    public string EventsKey { get; set; } = string.Empty;

    /// <summary>The <c>portalClientId</c> an event for THIS application carries. Blank: the Keycloak client id
    /// (<c>Keycloak:ClientId</c>) is used; both blank: the field is not checked.</summary>
    public string PortalClientId { get; set; } = string.Empty;

    /// <summary>The <c>aud</c> a Google ID token must carry: this service's ORIGIN(S), no path, comma-separated.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The one service-account address allowed to call with a Google ID token. Passing the platform's
    /// invoker check says nothing about WHICH caller this is; this is the application's own half.</summary>
    public string AllowedCallerEmail { get; set; } = string.Empty;

    /// <summary>OIDC discovery document for Google. Overridable so a test can use a local one.</summary>
    public string MetadataAddress { get; set; } = "https://accounts.google.com/.well-known/openid-configuration";

    public IReadOnlyList<string> Audiences =>
        Audience.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool KeyConfigured => !string.IsNullOrWhiteSpace(EventsKey);

    public bool GoogleConfigured => Audiences.Count > 0 && !string.IsNullOrWhiteSpace(AllowedCallerEmail);

    public bool IsEnabled => KeyConfigured || GoogleConfigured;
}

/// <summary>Scheme and principal names for the session-ended surface.</summary>
public static class AuthBackendAuthenticationDefaults
{
    public const string KeySchemeName = "AuthBackendEventsKey";
    public const string GoogleSchemeName = "AuthBackendIdToken";

    /// <summary>Both schemes, for <c>[Authorize(AuthenticationSchemes = …)]</c>: either one authenticates the call.</summary>
    public const string Schemes = KeySchemeName + "," + GoogleSchemeName;

    public const string KeyHeader = "X-Auth-Events-Key";

    /// <summary>The fixed identity of a successfully authenticated sender — neither a person nor any user's
    /// role, and satisfying no policy but <see cref="Policies.AuthBackendServiceAccess"/>.</summary>
    public const string ServiceIdentityName = "auth-backend-service";

    /// <summary>The surface name carried as a structured field on every log line of this door.</summary>
    public const string SurfaceName = "session-ended";
}

/// <summary>Authenticates <c>X-Auth-Events-Key</c> against the configured key, in constant time. No header, or
/// no key configured: no result (another scheme may still authenticate). A wrong key fails, with a reason slug
/// in the log and never the value.</summary>
public sealed class AuthBackendEventsKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<AuthBackendIntegrationOptions> _integration;

    public AuthBackendEventsKeyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IOptionsMonitor<AuthBackendIntegrationOptions> integration)
        : base(options, logger, encoder) => _integration = integration;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var settings = _integration.CurrentValue;
        var supplied = Request.Headers[AuthBackendAuthenticationDefaults.KeyHeader].ToString();
        if (supplied.Length == 0 || !settings.KeyConfigured)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!KeyMatches(supplied, settings.EventsKey))
        {
            AuthBackendRejection.Log(Logger, Context, AuthBackendAuthenticationDefaults.KeySchemeName, "events-key-mismatch");
            return Task.FromResult(AuthenticateResult.Fail("The events key does not match."));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            AuthBackendRejection.ServicePrincipal(Scheme.Name), Scheme.Name)));
    }

    /// <summary>Constant time and independent of the supplied key's length: both sides are hashed first.</summary>
    public static bool KeyMatches(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

internal static class AuthBackendRejection
{
    public static ClaimsPrincipal ServicePrincipal(string schemeName) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, AuthBackendAuthenticationDefaults.ServiceIdentityName),
            new Claim(ClaimTypes.NameIdentifier, AuthBackendAuthenticationDefaults.ServiceIdentityName),
        ],
        schemeName));

    /// <summary>One rejection line. The surface and scheme are FIELDS, not text in the template; the route is a
    /// TEMPLATE, never the path; the trace id is the one the response envelope carries. Not the key, the token,
    /// a header, or an exception message (IdentityModel's messages quote claim values).</summary>
    public static void Log(ILogger logger, HttpContext httpContext, string scheme, string reason) =>
        logger.LogWarning(
            "Rejected an inbound call. Surface={Surface} Scheme={Scheme} Reason={Reason} Route={Route} TraceId={TraceId}",
            AuthBackendAuthenticationDefaults.SurfaceName, scheme, reason,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(none)", httpContext.TraceIdentifier);
}

/// <summary>
/// Answers 404 for the whole <c>/api/integration/auth-backend</c> surface while neither the key nor the Google caller
/// is configured, BEFORE authentication runs: an unconfigured door must not say "401, there is something here to
/// guess at". Middleware, not an MVC filter, on purpose: the policy on the controller is enforced by the
/// authorization MIDDLEWARE, which runs ahead of every MVC filter, so a filter would only ever see the call after a
/// 401 had already been written.
/// </summary>
public sealed class AuthBackendIntegrationEnabledMiddleware
{
    private const string Prefix = "/api/integration/auth-backend";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AuthBackendIntegrationOptions> _options;

    public AuthBackendIntegrationEnabledMiddleware(RequestDelegate next, IOptionsMonitor<AuthBackendIntegrationOptions> options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase) && !_options.CurrentValue.IsEnabled)
        {
            // Bodiless on purpose: the status-code pages middleware ahead of this one wraps it in the envelope.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _next(context);
    }
}

/// <summary>
/// The Google-ID-token way in, ported from CAD's <c>GoogleIdTokenScheme</c>: signature, issuer, audience and
/// expiry from Google's keys, THEN the caller's verified e-mail equal to the one allowed service account.
/// Neither check stands in for the other. Registered as an ADDITIONAL scheme, opt-in per endpoint; Keycloak
/// stays the default for everything else.
/// </summary>
public static class AuthBackendGoogleIdTokenScheme
{
    public const string GoogleIssuer = "https://accounts.google.com";

    public static AuthenticationBuilder AddAuthBackendGoogleIdToken(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        builder.Services.Configure<AuthBackendIntegrationOptions>(configuration.GetSection(AuthBackendIntegrationOptions.SectionName));
        const string scheme = AuthBackendAuthenticationDefaults.GoogleSchemeName;

        builder.AddJwtBearer(scheme, _ => { });
        builder.Services.AddOptions<JwtBearerOptions>(scheme)
            .Configure<IOptions<AuthBackendIntegrationOptions>, ILoggerFactory>((options, integration, loggers) =>
            {
                var settings = integration.Value;
                var logger = loggers.CreateLogger("AjBoilerplate.Api.Identity." + scheme);

                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = true;
                options.SaveToken = false;

                // The sender gives up after 10 s, so a discovery fetch that hangs must fail inside that window.
                options.BackchannelTimeout = TimeSpan.FromSeconds(8);
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = GoogleIssuer,
                    ValidateAudience = true,
                    ValidAudiences = settings.Audiences,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromMinutes(1),
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Not configured: nothing to validate against, so nothing is accepted — and no result at all
                        // when no token was even offered, so the events-key caller is never logged as a rejection.
                        if (!settings.GoogleConfigured)
                        {
                            if (context.Request.Headers.Authorization.Count > 0)
                            {
                                AuthBackendRejection.Log(logger, context.HttpContext, scheme, "not-configured");
                            }

                            context.NoResult();
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        AuthBackendRejection.Log(logger, context.HttpContext, scheme, ReasonSlug(context.Exception));
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var reason = CallerProblem(context.Principal, settings.AllowedCallerEmail);
                        if (reason is not null)
                        {
                            AuthBackendRejection.Log(logger, context.HttpContext, scheme, reason);
                            context.Fail("The caller is not the allowed service account.");
                            return Task.CompletedTask;
                        }

                        // A principal of this scheme's own, carrying none of the token's claims.
                        context.Principal = AuthBackendRejection.ServicePrincipal(scheme);
                        return Task.CompletedTask;
                    },
                };
            });

        return builder;
    }

    /// <summary>The second required check. Returns a reason slug when the caller is not acceptable.</summary>
    public static string? CallerProblem(ClaimsPrincipal? principal, string allowedEmail)
    {
        if (!string.Equals(principal?.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "email-not-verified";
        }

        var email = principal?.FindFirst("email")?.Value;
        return !string.IsNullOrWhiteSpace(email)
            && string.Equals(email.Trim(), allowedEmail.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : "caller-not-allowed";
    }

    /// <summary>Distinguishes the checks a single "claims mismatch" would fold together. Derived from the
    /// exception TYPE; its message is never read.</summary>
    public static string ReasonSlug(Exception exception) => exception switch
    {
        SecurityTokenExpiredException => "token-expired",
        SecurityTokenNotYetValidException => "token-not-yet-valid",
        SecurityTokenInvalidAudienceException => "audience-mismatch",
        SecurityTokenInvalidIssuerException => "issuer-mismatch",
        SecurityTokenSignatureKeyNotFoundException => "signing-key-not-found",
        SecurityTokenInvalidSignatureException => "signature-invalid",
        SecurityTokenInvalidAlgorithmException => "algorithm-not-allowed",
        SecurityTokenNoExpirationException => "no-expiration",
        SecurityTokenMalformedException => "malformed-token",

        // Google's discovery document or key set could not be fetched: OUR egress failing, not the caller's token.
        InvalidOperationException or HttpRequestException or TaskCanceledException => "signing-keys-unavailable",
        _ => "token-invalid",
    };
}
