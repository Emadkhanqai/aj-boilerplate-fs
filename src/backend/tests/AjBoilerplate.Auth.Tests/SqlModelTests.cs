using AjBoilerplate.Auth.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AjBoilerplate.Auth.Tests;

/// <summary>Offline checks of the session table: no server is contacted.</summary>
public sealed class SqlModelTests
{
    private static AuthDbContext Context() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=auth").Options);

    [Fact]
    public void The_session_table_is_in_the_auth_schema_with_a_key_column_exactly_as_wide_as_a_storage_key()
    {
        using var db = Context();
        var entity = db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Single();
        var key = entity.FindProperty(nameof(SessionRow.SessionKey))!;

        Assert.Equal("auth", entity.GetSchema());
        Assert.Equal("Sessions", entity.GetTableName());
        // 43 = base64url of a SHA-256 digest. A wider fixed-width column pads with spaces and a key read back no longer equals the key written.
        Assert.Equal("char(43)", key.GetColumnType());
        Assert.Equal(43, AjBoilerplate.Auth.Security.SessionIds.StorageKey("anything").Length);
        Assert.Equal("Latin1_General_BIN2", key.GetCollation());
    }

    [Fact]
    public void The_committed_migration_matches_the_model_so_no_migration_is_pending()
    {
        using var db = Context();
        Assert.False(db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>().Migrations.Count == 0); // control: the migration was found
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
