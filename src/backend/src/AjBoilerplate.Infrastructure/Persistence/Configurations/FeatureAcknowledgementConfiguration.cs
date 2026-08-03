using AjBoilerplate.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>MSSQL mapping for <see cref="FeatureAcknowledgement"/>.</summary>
public sealed class FeatureAcknowledgementConfiguration : IEntityTypeConfiguration<FeatureAcknowledgement>
{
    public void Configure(EntityTypeBuilder<FeatureAcknowledgement> builder)
    {
        builder.ToTable("feat_Acknowledgements");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        // The identity provider's subject identifier, stored verbatim. There is deliberately no
        // foreign key: users live in the IdP, not in this schema.
        builder.Property(a => a.UserId)
            .HasMaxLength(FeatureAcknowledgement.MaxUserIdLength)
            .IsRequired();

        builder.Property(a => a.FeatureId)
            .IsRequired();

        builder.Property(a => a.AcknowledgedAt)
            .HasColumnType("datetimeoffset(7)")
            .IsRequired();

        // Deleting an announcement takes its acknowledgements with it — they mean nothing without it,
        // and leaving them would block the delete or strand orphan rows.
        builder.HasOne(a => a.Feature)
            .WithMany()
            .HasForeignKey(a => a.FeatureId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        AuditedEntityConfiguration.Apply(builder);

        // ONE acknowledgement per (user, announcement). This is a correctness constraint, not a
        // tuning choice: it is what makes a dismissal permanent and un-duplicable even when two
        // requests race past the application's own idempotency check, and it is also the index the
        // "has this user seen it?" lookup seeks on.
        builder.HasIndex(a => new { a.UserId, a.FeatureId })
            .IsUnique()
            .HasDatabaseName("IX_feat_Acknowledgements_User_Feature");
    }
}
