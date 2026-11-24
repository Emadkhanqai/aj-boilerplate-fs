using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AjBoilerplate.Auth.Redis;

public static class RedisServiceCollectionExtensions
{
    public const string ConnectionStringName = "Redis";
    public const string CaCertPemKey = "Redis:CaCertPem";

    /// <summary>Registers Redis: the session cache in front of SQL, login state, the refresh lock and the revoked markers.</summary>
    public static IServiceCollection AddAuthRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is required.");
        }

        var options = RedisConnectionOptionsFactory.Create(connectionString, configuration[CaCertPemKey]);

        // AbortOnConnectFail=false: returns immediately and keeps reconnecting in the background.
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));

        services.AddSingleton(sp =>
            new SessionTokenProtector(sp.GetRequiredService<IOptions<AuthOptions>>().Value.Session.EncryptionKey));

        services.AddSingleton<RedisSessionStore>();
        services.AddKeyedSingleton<ISessionStore, CacheSessionStore>(DurableSessionStore.CacheServiceKey);
        services.AddSingleton<ILoginStateStore, RedisLoginStateStore>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        services.AddSingleton<IRevokedSessionMarks, RedisRevokedSessionMarks>();

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
        return services;
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F0} ms");
        }
        catch (RedisException ex)
        {
            return HealthCheckResult.Unhealthy("redis unreachable", ex);
        }
    }
}
