using AjBoilerplate.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AjBoilerplate.Infrastructure.Health;

/// <summary>Readiness probe: the API is ready only when its database is reachable.</summary>
public sealed class DbContextHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public DbContextHealthCheck(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Database reachable.")
            : HealthCheckResult.Unhealthy("Database unreachable.");
    }
}
