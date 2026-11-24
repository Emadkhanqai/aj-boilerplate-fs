using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AjBoilerplate.Auth.Sql;

/// <summary>System of record for sessions (schema <c>auth</c>). Redis is a cache in front of it.</summary>
public sealed class AuthDbContext : DbContext
{
    public const string Schema = "auth";

    /// <summary>Migration history lives in the service's own schema, so it never collides with another service in a shared database.</summary>
    public const string HistoryTable = "__EFMigrationsHistory";

    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    internal DbSet<SessionRow> Sessions => Set<SessionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SessionRow>(e =>
        {
            e.ToTable("Sessions", Schema);
            e.HasKey(x => x.SessionKey);

            // SessionIds.StorageKey is base64url SHA-256: exactly 43 ASCII characters. A fixed-width column of
            // exactly that width never pads, so a key read back from the table is the key that was written.
            // Binary collation: two keys differing only in case are never the same row.
            e.Property(x => x.SessionKey).HasColumnType("char(43)").UseCollation("Latin1_General_BIN2");
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            e.Property(x => x.KeycloakJwt).IsRequired();

            e.HasIndex(x => x.Subject).HasDatabaseName("IX_Sessions_Subject");
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("IX_Sessions_ExpiresAt");
        });
    }
}

/// <summary>Lets <c>dotnet ef</c> build a context without running the host. Reads <c>AUTH_DB_CONNECTION</c>.</summary>
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public const string ConnectionEnvironmentVariable = "AUTH_DB_CONNECTION";

    public AuthDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(
                string.IsNullOrWhiteSpace(connection) ? "Server=(localdb)\\mssqllocaldb;Database=auth" : connection,
                sql => sql.MigrationsHistoryTable(AuthDbContext.HistoryTable, AuthDbContext.Schema))
            .Options;
        return new AuthDbContext(options);
    }
}
