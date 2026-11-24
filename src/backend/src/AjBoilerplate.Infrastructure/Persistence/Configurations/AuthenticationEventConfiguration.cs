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
        builder.Property(e => e.DisplayName).HasMaxLength(AuthenticationEvent.MaxDisplayNameLength);
        builder.Property(e => e.StartedAt).IsRequired().HasColumnType("datetimeoffset(7)");
        builder.Property(e => e.RecordedAt).IsRequired().HasColumnType("datetimeoffset(7)");

        // How the session ended, on the same row. All null while it is open. The conditional UPDATE in
        // AuthenticationEventRepository.EndAsync (EndedAt IS NULL) is what makes an ending happen once.
        builder.Property(e => e.EndedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(e => e.EndReason).HasMaxLength(32).IsUnicode(false);
        builder.Property(e => e.EndReasonDetail).HasMaxLength(AuthenticationEvent.MaxEndReasonDetailLength).IsUnicode(false);
        builder.Ignore(e => e.Result);

        // THE GUARANTEE ITSELF: one row per gateway session. The in-memory "already recorded" cache
        // only keeps the database out of the hot path; two instances, or two first requests racing,
        // are decided here.
        builder.HasIndex(e => e.SessionId).IsUnique().HasDatabaseName("IX_AuthenticationEvents_SessionId");
        builder.HasIndex(e => e.StartedAt).HasDatabaseName("IX_AuthenticationEvents_StartedAt");
        builder.HasIndex(e => e.Subject).HasDatabaseName("IX_AuthenticationEvents_Subject");
    }
}
