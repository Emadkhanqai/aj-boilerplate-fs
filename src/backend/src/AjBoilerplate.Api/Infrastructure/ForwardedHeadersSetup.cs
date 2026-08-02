using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>Bound from the <c>ForwardedHeaders</c> config section.</summary>
public sealed class ForwardedHeadersConfigOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Off by default: with no reverse proxy in front, honouring <c>X-Forwarded-For</c>
    /// would let any caller spoof their client IP and defeat the anonymous rate-limit partition. Only
    /// a deployment that actually sits behind a trusted proxy (Cloud Run, App Service, a load
    /// balancer) turns this on.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many proxy hops to trust in <c>X-Forwarded-For</c>, right-to-left. 1 is correct
    /// for a single trusted proxy: the last entry is the address that proxy saw, i.e. the real
    /// client. Raising it without also populating <see cref="KnownProxies"/> /
    /// <see cref="KnownNetworks"/> lets a client forge additional hops.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Optional explicit trusted proxy IPs. When empty the known-proxies/known-networks
    /// lists are cleared so <see cref="ForwardLimit"/> alone bounds trust — a documented, safe
    /// posture when the proxy address is not fixed or known in this environment.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Optional trusted proxy networks in CIDR form (e.g. <c>10.0.0.0/8</c>).</summary>
    public string[] KnownNetworks { get; set; } = [];
}

/// <summary>
/// Reverse-proxy header handling so the rate limiter — and anything else reading
/// <c>Connection.RemoteIpAddress</c> — partitions by the REAL client IP rather than the shared proxy
/// egress IP. Behind a managed platform or load balancer every request arrives from the proxy's
/// address, so without this the anonymous partition collapses and all callers share one bucket.
/// Opt-in via configuration so a proxy-less deployment is never weakened.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ForwardedHeadersConfigOptions.SectionName).Get<ForwardedHeadersConfigOptions>()
            ?? new ForwardedHeadersConfigOptions();
        if (!options.Enabled)
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(fho =>
        {
            fho.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            fho.ForwardLimit = options.ForwardLimit;

            // Clearing the defaults (loopback only) is deliberate: with no fixed proxy address to
            // pin, ForwardLimit is what bounds how far X-Forwarded-For is trusted. When proxy
            // IPs/networks ARE known they are added below and tighten trust further.
            fho.KnownProxies.Clear();
            fho.KnownIPNetworks.Clear();
            foreach (var proxy in options.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    fho.KnownProxies.Add(address);
                }
            }

            foreach (var network in options.KnownNetworks)
            {
                // Fully qualified: Microsoft.AspNetCore.HttpOverrides also defines an IPNetwork.
                if (System.Net.IPNetwork.TryParse(network, out var ipNetwork))
                {
                    fho.KnownIPNetworks.Add(ipNetwork);
                }
            }
        });

        return services;
    }

    /// <summary>Applies the forwarded-headers middleware only when <c>ForwardedHeaders:Enabled</c>
    /// is true. Must run before anything that reads the client IP (request logging, the rate
    /// limiter).</summary>
    public static IApplicationBuilder UseApiForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (configuration.GetSection(ForwardedHeadersConfigOptions.SectionName).GetValue<bool>("Enabled"))
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
