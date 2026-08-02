using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the model without running the API.
/// <c>dotnet ef migrations add</c> never connects, so the fallback string below only has to be
/// syntactically valid; <c>dotnet ef database update</c> does connect, so set
/// <c>APP_DB_CONNECTION</c> before running it (docker-compose.yml's `migrate` service does).
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Environment variable the EF Core tooling reads its connection string from. NOT
    /// <c>ConnectionStrings__Default</c> — that is only wired into the running app via
    /// Program.cs/DI, which the design-time tooling never starts.</summary>
    public const string ConnectionEnvironmentVariable = "APP_DB_CONNECTION";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)
            ?? "Server=localhost;Database=AppDb;Trusted_Connection=True;TrustServerCertificate=True;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
