using AjBoilerplate.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>MSSQL mapping for <see cref="OutboxMessage"/> — a minimal transactional-outbox row.</summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.MessageType)
            .HasMaxLength(80)
            .IsRequired();

        // Unbounded — the event payload.
        builder.Property(m => m.PayloadJson)
            .IsRequired();

        builder.Property(m => m.OccurredAtUtc)
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

        // The dispatcher polls pending messages oldest-first.
        builder.HasIndex(m => new { m.Status, m.OccurredAtUtc });
        builder.HasIndex(m => m.CorrelationId);
    }
}
