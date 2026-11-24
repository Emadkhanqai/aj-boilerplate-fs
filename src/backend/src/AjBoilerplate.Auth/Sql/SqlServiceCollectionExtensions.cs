using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Sql;

public static class SqlServiceCollectionExtensions
{
    public const string ConnectionStringName = "AuthDb";

    /// <summary>Registers the durable store: SQL is the system of record and the Redis cache registered by <c>AddAuthRedis</c> sits in front.</summary>
    public static IServiceCollection AddAuthSql(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is required.");
        }

        // A pooled factory, not a scoped context: the session store is a singleton and opens a short-lived context per operation.
        services.AddPooledDbContextFactory<AuthDbContext>(o => o
            .UseSqlServer(connectionString, sql => sql
                .MigrationsHistoryTable(AuthDbContext.HistoryTable, AuthDbContext.Schema)
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorNumbersToAdd: null)));

        services.AddSingleton<SqlSessionStore>();
        services.AddSingleton<ISessionStore>(sp => new DurableSessionStore(
            sp.GetRequiredService<SqlSessionStore>(),
            sp.GetRequiredKeyedService<ISessionStore>(DurableSessionStore.CacheServiceKey),
            sp.GetRequiredService<ILogger<DurableSessionStore>>())
        {
            IdleTimeout = sp.GetRequiredService<IOptions<AuthOptions>>().Value.Session.IdleTimeout,
        });

        services.AddHostedService<ExpiredSessionSweeper>();
        services.AddHealthChecks().AddCheck<AuthDbHealthCheck>("auth-db", tags: ["ready"]);
        return services;
    }
}

internal sealed class AuthDbHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<AuthDbContext> _contexts;

    public AuthDbHealthCheck(IDbContextFactory<AuthDbContext> contexts) => _contexts = contexts;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("auth database unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("auth database unreachable", ex);
        }
    }
}
