using AjBoilerplate.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>
/// The shared column mapping for every <see cref="AuditedEntity"/>, applied from each entity's own
/// configuration. One place decides how <c>CreatedAt</c>/<c>UpdatedAt</c>/<c>RowVersion</c> are
/// stored, so a new aggregate cannot map them subtly differently.
/// </summary>
public static class AuditedEntityConfiguration
{
    public static void Apply<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditedEntity
    {
        // datetimeoffset(7): the offset is stored, so the value round-trips unambiguously and needs
        // none of the Kind-repair conversion AppDbContext applies to plain DateTime columns.
        builder.Property(e => e.CreatedAt)
            .HasColumnType("datetimeoffset(7)")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnType("datetimeoffset(7)");

        // A SQL Server rowversion column. The database issues and advances the value, and EF Core
        // adds the loaded original to the WHERE clause of every UPDATE and DELETE — so a write that
        // lost a race matches zero rows and raises DbUpdateConcurrencyException, enforced by the
        // engine rather than by a comparison the application made in an earlier statement.
        builder.Property(e => e.RowVersion)
            .IsRowVersion();
    }
}
