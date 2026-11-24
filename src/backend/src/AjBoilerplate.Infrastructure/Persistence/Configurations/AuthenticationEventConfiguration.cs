using AjBoilerplate.Domain.AuthenticationHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

public sealed class AuthenticationEventConfiguration : IEntityTypeConfiguration<AuthenticationEvent>
{
    public void Configure(EntityTypeBuilder<AuthenticationEvent> builder)
    {
        builder.ToTable("AuthenticationEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // The identity provider's subject, stored verbatim — no foreign key, because users live in
        // the IdP and not in this schema.
        builder.Property(e => e.Subject).IsRequired().HasMaxLength(AuthenticationEvent.MaxSubjectLength);
        builder.Property(e => e.SessionId).IsRequired().HasMaxLength(AuthenticationEvent.MaxSessionIdLength).IsUnicode(false);
        builder.Property(e => e.StartedAt).IsRequired().HasColumnType("datetimeoffset(7)");
        builder.Property(e => e.RecordedAt).IsRequired().HasColumnType("datetimeoffset(7)");

        // THE GUARANTEE ITSELF: one row per gateway session. The in-memory "already recorded" cache
        // only keeps the database out of the hot path; two instances, or two first requests racing,
        // are decided here.
        builder.HasIndex(e => e.SessionId).IsUnique().HasDatabaseName("IX_AuthenticationEvents_SessionId");
        builder.HasIndex(e => e.Subject).HasDatabaseName("IX_AuthenticationEvents_Subject");
    }
}
