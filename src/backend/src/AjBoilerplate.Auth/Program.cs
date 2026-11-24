using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Observability;
using AjBoilerplate.Auth.Redis;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.SessionEvents;
using AjBoilerplate.Auth.Sessions;
using AjBoilerplate.Auth.Sql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Context;
using Serilog.Events;

namespace AjBoilerplate.Auth;

/// <summary>
/// The Auth service: server-side Authorization Code + PKCE sign-in, an opaque session cookie, and the
/// <c>/auth/validate</c> answer nginx's <c>auth_request</c> turns into <c>Authorization: Bearer</c>.
/// The wire contract is docs/auth/gateway-contract.md. An explicit Main in a namespace, not top-level
/// statements, so this assembly's entry point is not a second global <c>Program</c> beside the API's.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var app = Build(args);
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

        // Same logging stack as the API (Serilog, console sink). Request/response headers are never logged:
        // a logged Authorization, X-Auth-Jwt or Cookie is a credential leak. Properties are printed so the
        // CorrelationId pushed below reaches every line of a request.
        builder.Host.UseSerilog((context, loggerConfig) => ConfigureLogger(loggerConfig, context.Configuration));

        builder.Services.AddSingleton<IValidateOptions<AuthOptions>>(new AuthOptionsValidator(builder.Environment.IsDevelopment()));
        builder.Services.AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PortalRegistry>();

        builder.Services.AddHttpClient<IEntraClient, EntraClient>((sp, http) =>
            http.Timeout = sp.GetRequiredService<IOptions<AuthOptions>>().Value.Entra.Timeout);
        builder.Services.AddHttpClient<IKeycloakExchanger, KeycloakExchanger>((sp, http) =>
            http.Timeout = sp.GetRequiredService<IOptions<AuthOptions>>().Value.Keycloak.Timeout);

        // Missing connection strings throw here, at start-up, not on the first request.
        builder.Services.AddAuthRedis(builder.Configuration);
        builder.Services.AddAuthSql(builder.Configuration);

        builder.Services.AddSingleton<RolesVersion>();
        builder.Services.AddSingleton<PortalSessionId>();
        builder.Services.AddScoped<SessionValidator>();
        builder.Services.AddScoped<SignInService>();
        builder.Services.AddScoped<ForceRefreshHandler>();
        builder.Services.AddScoped<HandoffService>();
        builder.Services.AddScoped<UserAdminService>();

        // The directory behind /internal/admin: the Keycloak Admin REST API once a dedicated admin client is configured, else a stand-in
        // that answers 501 rather than an empty list.
        builder.Services.AddHttpClient(KeycloakAdminClient.HttpClientName, http =>
            http.Timeout = builder.Configuration.GetSection($"{AuthOptions.SectionName}:Admin:Keycloak:Timeout").Get<TimeSpan?>() ?? TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<IDirectoryAdmin>(sp =>
            sp.GetRequiredService<IOptions<AuthOptions>>().Value.Admin.Keycloak.IsConfigured
                ? ActivatorUtilities.CreateInstance<KeycloakAdminClient>(sp)
                : new NotConfiguredDirectoryAdmin());

        // Google ID token checks: the /auth/events push and the opt-in admin caller. Disabled until configured, but registered
        // always, so the endpoints can refuse (404) rather than fail to resolve.
        builder.Services.AddHttpClient(GoogleIdTokenVerifier.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<GoogleIdTokenVerifier>();

        builder.Services.AddSessionEndedDelivery();

        var app = builder.Build();

        app.Use(async (http, next) =>
        {
            // nginx's $request_id arrives as X-Request-Id and is printed on its error pages, so a symptom in a
            // browser joins a line here. Sanitised, not trusted: anything that is not a short id-shaped value is
            // replaced by Kestrel's own trace id.
            var supplied = http.Request.Headers["X-Request-Id"].ToString();
            var correlation = IsIdShaped(supplied) ? supplied : http.TraceIdentifier;

            // Ambient, for the one place far from HTTP that needs it: the session-ended event a request causes carries it.
            RequestCorrelation.Current = correlation;
            using (LogContext.PushProperty("CorrelationId", correlation))
            {
                await next(http);
            }
        });

        // The route TEMPLATE, never the raw path: a query string carries the OAuth code and state, and a path
        // segment can carry a subject id.
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {Route} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diagnosticContext, http) =>
                diagnosticContext.Set("Route", (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)");
        });

        LogInsecureUpstreams(app.Services.GetRequiredService<IOptions<AuthOptions>>().Value, app.Logger);

        // Liveness never touches dependencies (a Redis blip must not restart instances); readiness does.
        app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

        app.MapAuthEndpoints();
        app.MapAdminEndpoints();
        return app;
    }

    /// <summary>
    /// The framework's own "Request starting" line carries the full path AND query string at Information, which
    /// here is the OAuth <c>code</c> and <c>state</c> on /auth/callback and the subject id on the admin route.
    /// So <c>Microsoft.AspNetCore</c> is held at Warning by default; configuration may still override it.
    /// (Serilog ignores the <c>Logging:LogLevel</c> section, so that section alone would not do this.)
    /// </summary>
    internal static LoggerConfiguration ConfigureLogger(LoggerConfiguration loggerConfig, IConfiguration configuration) => loggerConfig
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");

    internal static bool IsIdShaped(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 64
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    private static void LogInsecureUpstreams(AuthOptions options, Microsoft.Extensions.Logging.ILogger logger)
    {
        foreach (var (key, url) in new[]
        {
            ("Auth:Entra:Authority", options.Entra.Authority),
            ("Auth:Entra:TokenEndpointUrl", options.Entra.TokenEndpointUrl),
            ("Auth:Keycloak:BaseUrl", options.Keycloak.BaseUrl),
            ("Auth:Keycloak:TokenEndpointUrl", options.Keycloak.TokenEndpointUrl),
            ("Auth:Admin:Keycloak:BaseUrl", options.Admin.Keycloak.BaseUrl),
            ("Auth:Portal:SessionEvents:Url", options.Portal.SessionEvents.Url),
        }.Concat(options.Portals.Select((p, i) => ($"Auth:Portals:{i}:SessionEvents:Url", p.SessionEvents.Url))))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                logger.LogWarning(
                    "Insecure upstream in use: {Setting} is plain http to {UpstreamHost}. Permitted only by Auth:AllowInsecureUpstreams or a Development host.",
                    key,
                    uri.Host);
            }
        }
    }
}
