using AjBoilerplate.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>MSSQL mapping for <see cref="FeatureAnnouncement"/>.</summary>
public sealed class FeatureAnnouncementConfiguration : IEntityTypeConfiguration<FeatureAnnouncement>
{
    public void Configure(EntityTypeBuilder<FeatureAnnouncement> builder)
    {
        builder.ToTable("feat_Features");
        builder.HasKey(f => f.Id);

        // Client-assigned Guid (the domain factory generates it), so an announcement's identity is
        // known before it is persisted.
        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.Key)
            .HasMaxLength(FeatureAnnouncement.MaxKeyLength)
            .IsRequired();

        builder.Property(f => f.TitleEn)
            .HasMaxLength(FeatureAnnouncement.MaxTitleLength)
            .IsRequired();

        builder.Property(f => f.TitleAr)
            .HasMaxLength(FeatureAnnouncement.MaxTitleLength);

        builder.Property(f => f.BodyEn)
            .HasMaxLength(FeatureAnnouncement.MaxBodyLength)
            .IsRequired();

        builder.Property(f => f.BodyAr)
            .HasMaxLength(FeatureAnnouncement.MaxBodyLength);

        builder.Property(f => f.PagesJson)
            .HasMaxLength(FeatureAnnouncement.MaxPagesJsonLength)
            .IsRequired();

        builder.Property(f => f.IsActive)
            .IsRequired();

        builder.Property(f => f.DisplayOrder)
            .IsRequired();

        AuditedEntityConfiguration.Apply(builder);

        // The stable handle whoever authors the next announcement migration writes against. Unique,
        // so a second migration cannot silently ship a duplicate of an existing announcement.
        builder.HasIndex(f => f.Key)
            .IsUnique()
            .HasDatabaseName("IX_feat_Features_Key");

        // Covers the hot path exactly: filter on IsActive, ordered by DisplayOrder. This lookup runs
        // on every navigation of every signed-in client, so it is the one query here worth an index.
        builder.HasIndex(f => new { f.IsActive, f.DisplayOrder })
            .HasDatabaseName("IX_feat_Features_Active_Order");
    }
}
