using AjBoilerplate.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AjBoilerplate.Infrastructure.Persistence.Configurations;

/// <summary>MSSQL mapping for <see cref="IdempotencyRecord"/> — the stored response of an unsafe
/// request the caller labelled with an <c>Idempotency-Key</c>.</summary>
public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedNever();

        // The identity provider's subject identifier, stored verbatim — no foreign key, because
        // users live in the IdP and not in this schema (same reasoning as feat_Acknowledgements).
        builder.Property(r => r.Scope)
            .HasMaxLength(IdempotencyRecord.MaxScopeLength)
            .IsRequired();

        builder.Property(r => r.Key)
            .HasMaxLength(IdempotencyRecord.MaxKeyLength)
            .IsRequired();

        builder.Property(r => r.Method)
            .HasMaxLength(IdempotencyRecord.MaxMethodLength)
            .IsRequired();

        builder.Property(r => r.Path)
            .HasMaxLength(IdempotencyRecord.MaxPathLength)
            .IsRequired();

        builder.Property(r => r.RequestHash)
            .HasMaxLength(IdempotencyRecord.RequestHashLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(r => r.ResponseContentType)
            .HasMaxLength(IdempotencyRecord.MaxContentTypeLength);

        builder.Property(r => r.ResponseLocation)
            .HasMaxLength(IdempotencyRecord.MaxLocationLength);

        // Unbounded — the captured response body, stored as the exact bytes the first caller
        // received so a replay cannot differ by a re-serialisation.
        builder.Property(r => r.ResponseBody);

        builder.Property(r => r.CompletedAt)
            .HasColumnType("datetimeoffset(7)");

        AuditedEntityConfiguration.Apply(builder);

        // THE GUARANTEE ITSELF. The application looks a key up before claiming it, but two concurrent
        // replays can both pass that lookup; this index is the only thing that stops both of them
        // executing. It is a correctness constraint, not a tuning choice — and it is also the index
        // the lookup seeks on.
        //
        // Scope leads the key deliberately: it makes the index selective per caller AND encodes the
        // privacy boundary, since a record can only ever be found under the identity that created it.
        builder.HasIndex(r => new { r.Scope, r.Key })
            .IsUnique()
            .HasDatabaseName("IX_IdempotencyRecords_Scope_Key");

        // Supports the retention sweep that prunes records past their useful life — see the
        // "Idempotency keys" section of src/backend/README.md. Without it that DELETE scans the table.
        builder.HasIndex(r => r.CreatedAt)
            .HasDatabaseName("IX_IdempotencyRecords_CreatedAt");
    }
}
