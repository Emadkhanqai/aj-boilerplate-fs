using AjBoilerplate.Domain.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>SAMPLE SLICE. MSSQL mapping for <see cref="Item"/> — the one table the
/// <c>InitialCreate</c> migration ships.</summary>
public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("Items");
        builder.HasKey(i => i.Id);

        // Client-assigned Guid (the domain factory generates it) — never a database-generated
        // sequential key, so an item's identity is known before it is persisted.
        builder.Property(i => i.Id)
            .ValueGeneratedNever();

        builder.Property(i => i.Name)
            .HasMaxLength(Item.MaxNameLength)
            .IsRequired();

        builder.Property(i => i.Description)
            .HasMaxLength(Item.MaxDescriptionLength);

        // Stored as its name, not its ordinal: reordering or inserting an enum member must never
        // silently reinterpret existing rows.
        builder.Property(i => i.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        AuditedEntityConfiguration.Apply(builder);

        // The list endpoint's default ordering (newest first) and its status filter.
        builder.HasIndex(i => i.CreatedAt);
        builder.HasIndex(i => i.Status);

        // Backs the `search` substring match on name. A LIKE '%term%' cannot seek on this index, but
        // it does let SQL Server scan the narrow index instead of the whole table; swap in full-text
        // search if the table ever grows past what that comfortably covers.
        builder.HasIndex(i => i.Name);
    }
}
