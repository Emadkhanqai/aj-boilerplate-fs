using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>Bound from the <c>RateLimiting</c> config section.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitLimit { get; set; } = 100;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Global fixed-window rate limiting. Requests are partitioned by authenticated user, falling back
/// to client IP for anonymous callers, so one noisy caller cannot exhaust another's quota.
/// Over-quota requests get a 429 — enveloped by <see cref="EnvelopeStatusCodePages"/>. Limits come
/// from configuration and are read per request via <see cref="IOptionsMonitor{TOptions}"/> so they
/// bind late and can be changed without a redeploy.
///
/// Behind a reverse proxy this depends on <see cref="ForwardedHeadersSetup"/> being enabled:
/// otherwise every anonymous request appears to come from the proxy's egress address and the whole
/// anonymous population shares one bucket.
/// </summary>
public static class RateLimiting
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitingOptions>>().CurrentValue;
                var partitionKey = context.User.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }
}
