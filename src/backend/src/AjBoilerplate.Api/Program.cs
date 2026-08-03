using AjBoilerplate.Api.Configuration;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Api.Messaging;
using AjBoilerplate.Api.Observability;
using AjBoilerplate.Application;
using AjBoilerplate.Infrastructure;
using AjBoilerplate.Infrastructure.Cloud;
using AjBoilerplate.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Secrets from the selected cloud's store (CLOUD_PROVIDER: gcp → Secret Manager, azure → Key
// Vault), loaded into IConfiguration before anything reads a connection string or a signing key.
// A no-op when the selected store is unconfigured, so local/test/offline falls back to appsettings,
// user-secrets, and environment variables.
builder.AddCloudSecrets();

// Structured logging. Console sink here; configuration can add more.
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// OpenTelemetry traces + metrics; the OTLP exporter attaches only when an endpoint is configured.
builder.Services.AddTelemetry(builder.Configuration);

builder.Services.AddControllers(options => options.Filters.Add<EnvelopeResultFilter>());

// Every response — success and error — is enveloped in ApiResponse<T>/ApiResponse.
// AddProblemDetails() stays registered only because UseExceptionHandler() throws at startup without
// an IProblemDetailsService; UnhandledExceptionHandler always handles first, so a problem+json body
// is never actually produced.
//
// HANDLER ORDER IS THE CONTRACT: Validation → Conflict → Forbidden → Unhandled. Each returns false
// for an exception it does not own, and Unhandled claims everything, so moving it up would swallow
// the three specific mappings and turn every 400/409/403 into a 500.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
builder.Services.AddExceptionHandler<ForbiddenExceptionHandler>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// OpenAPI, which the frontend generates its API types from — so it must describe the real wire
// shape, envelope included.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Named explicitly rather than left to Swashbuckle's default, which is the ASSEMBLY FULL NAME —
    // "AjBoilerplate.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null". That string is both
    // an unhelpful API title and a snapshot-churn source: the committed docs/api/openapi.json is
    // diffed byte for byte by CI, so an assembly version bump would fail the contract gate for a
    // change that alters no contract at all.
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AjBoilerplate API",
        Version = "v1",
        Description = "Every response is wrapped in the ApiResponse<T> envelope — see ADR-0005.",
    });

    options.OperationFilter<EnvelopeResponseSchemaFilter>();

    // Surfaces each endpoint's /// <summary> and DTO property docs. Both this project and
    // AjBoilerplate.Contracts set GenerateDocumentationFile — the Contracts .xml lands alongside
    // this one via the normal ProjectReference output copy. Guarded with File.Exists rather than
    // assumed: a missing doc file should leave the document undocumented, not crash startup.
    foreach (var xmlName in new[] { "AjBoilerplate.Api.xml", "AjBoilerplate.Contracts.xml" })
    {
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlName);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }
    }
});

// Clean Architecture composition root.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Liveness = the process is up; readiness = dependencies (the database) are reachable.
builder.Services.AddHealthChecks()
    .AddCheck<DbContextHealthCheck>("sql", tags: ["ready"]);

// Reverse-proxy forwarded-headers handling (opt-in via ForwardedHeaders:Enabled) so the rate
// limiter below partitions anonymous callers by the REAL client IP, not the shared proxy egress IP.
builder.Services.AddApiForwardedHeaders(builder.Configuration);

// Global fixed-window rate limiting (429 over quota).
builder.Services.AddApiRateLimiting(builder.Configuration);

// Cross-origin policy for the deployed frontend. Origins come from Cors:AllowedOrigins.
builder.Services.AddApiCors(builder.Configuration);

// Bearer authentication (Keycloak, or the selected cloud's IdP) + role-based authorization.
builder.Services.AddApiAuthentication(builder.Configuration);

// Idempotency-Key handling for unsafe requests (see IdempotencyMiddleware, wired below).
builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection(IdempotencyOptions.SectionName));

// Transactional-outbox dispatcher. A publish failure for one message, or a whole bad tick, never
// crashes this hosted service; it just retries on the next interval.
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

var app = builder.Build();

// --- Startup checks: fail (or warn) at BOOT, not on some later request ------------------------
// Log the selected cloud once, at Information. Which cloud's secret store and IdP a process is
// talking to is the single most useful line in a deployment's startup log, and the most confusing
// thing to be wrong about silently.
var selectedCloud = (builder.Configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions()).Resolve();
app.Logger.LogInformation("Cloud provider: {CloudProvider} (set CLOUD_PROVIDER to change).", selectedCloud);

// AddInfrastructure silently falls back to an in-memory distributed cache when
// ConnectionStrings:Redis is unset — correct for a single Development instance, but a deployed
// environment doing the same thing has a cache that stops being shared the moment there is more
// than one instance, with nothing to say so. The check runs here rather than inside
// AddInfrastructure because the container has not been built at registration time, so there is no
// ILogger<T> — this app's real Serilog-backed logger — available yet. app.Logger is.
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
{
    app.Logger.LogWarning(
        "ConnectionStrings:Redis is not configured outside Development — falling back to an in-memory " +
        "distributed cache. This is process-local and will NOT be shared across instances; configure " +
        "a managed Redis before running more than one instance.");
}
// ---------------------------------------------------------------------------------------------

// MIDDLEWARE ORDER IS LOAD-BEARING. Read the comments before reordering anything below.

// Forwarded headers must run before ANY middleware that reads the client IP (request logging and
// the rate limiter), so X-Forwarded-For is resolved into Connection.RemoteIpAddress up front. A
// no-op unless ForwardedHeaders:Enabled, so a proxy-less deployment is never weakened.
app.UseApiForwardedHeaders(builder.Configuration);

// Security headers first, so they stamp EVERY response — including error and 404 replies produced
// further down, which is exactly where they are most often missing.
app.UseMiddleware<SecurityHeadersMiddleware>();

// Structured HTTP request logging. The template logs {SanitizedPath} instead of
// Serilog.AspNetCore's default {RequestPath}: any credential that travels in the URL (a reset
// token, a signed link's signature, an OAuth code) would otherwise be written verbatim into the log
// on every request. See RequestLogSanitizer.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {SanitizedPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        diagnosticContext.Set(
            "SanitizedPath",
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value + httpContext.Request.QueryString.Value));
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Exception handling before anything that can throw downstream, and status-code pages immediately
// after, so a bodiless framework reply (401/403/404/405/415/429) still ships an envelope.
app.UseExceptionHandler();
app.UseStatusCodePages(context => EnvelopeStatusCodePages.HandleAsync(context.HttpContext));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors(Cors.FrontendPolicy);

// Rate limiting BEFORE authentication: rejecting an over-quota request should not first cost a
// signature validation and a JWKS lookup, which is precisely the work an attacker wants to force.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Idempotency-Key replay for unsafe requests. AFTER authentication and authorization, and that
// order is the security property: a key is scoped to the authenticated caller, so the principal must
// already be resolved — and an unauthorized caller must be rejected before it can claim a key or
// read back a stored response. Before MapControllers, because it has to buffer the response the
// endpoint produces.
app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// Liveness excludes all dependency checks; readiness runs the "ready"-tagged ones. "/health" is
// aliased to LIVENESS, not readiness, so a transient database blip cannot flap a healthy instance
// out of the load balancer's rotation for a dependency hiccup it would have recovered from.
//
// All three are exempt from the global rate limiter: a probe polls on its own fixed interval and a
// shared egress IP or a tight interval could otherwise exhaust the quota and take the instance out
// of rotation for a rate-limit hit rather than a real health problem.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).DisableRateLimiting();

await app.RunAsync();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry
/// point.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
