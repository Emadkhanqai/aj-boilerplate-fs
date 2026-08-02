using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AjBoilerplate.Api.Observability;

/// <summary>
/// OpenTelemetry tracing + metrics with ASP.NET Core and HttpClient instrumentation. Telemetry is
/// exported over OTLP when an endpoint is configured — the provider-neutral protocol every major
/// backend ingests (directly or via the OpenTelemetry Collector), which is what keeps this code
/// free of any cloud branch. With no endpoint configured (local, tests, offline) the pipeline is
/// fully instrumented but exports nowhere, so the API starts clean with no external dependency.
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceName = "AjBoilerplate.Api";

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuration["Otlp:Endpoint"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }
}
