using AjBoilerplate.Domain.Messaging.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>MSSQL mapping for <see cref="InboxMessage"/> — the idempotency record for an inbound
/// integration event, keyed by the originating system's own event id.</summary>
public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.SourceEventId)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(m => m.EventType)
            .HasMaxLength(80)
            .IsRequired();

        // Unbounded — the event payload.
        builder.Property(m => m.PayloadJson)
            .IsRequired();

        builder.Property(m => m.ReceivedAtUtc)
            .HasColumnType("datetime2");

        builder.Property(m => m.ProcessedAtUtc)
            .HasColumnType("datetime2");

        builder.Property(m => m.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(m => m.Error)
            .HasMaxLength(2000);

        builder.Property(m => m.EventVersion)
            .IsRequired()
            .HasDefaultValue(1);

        builder.Property(m => m.CorrelationId)
            .HasMaxLength(64);

        // The dedup guarantee lives here — the unique index is the actual guard, not just the
        // application-layer lookup-before-insert, which two concurrent replays can both pass.
        builder.HasIndex(m => m.SourceEventId).IsUnique();

        // Polling-friendly shape, matching the outbox's (Status, OccurredAtUtc) index.
        builder.HasIndex(m => new { m.Status, m.ReceivedAtUtc });
        builder.HasIndex(m => m.CorrelationId);
    }
}
