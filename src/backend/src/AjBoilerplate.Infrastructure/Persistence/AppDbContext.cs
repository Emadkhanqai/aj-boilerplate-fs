using AjBoilerplate.Domain.Items;
using AjBoilerplate.Domain.Messaging;
using AjBoilerplate.Domain.Messaging.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AjBoilerplate.Infrastructure.Persistence;

/// <summary>
/// The single persistence context. The schema it describes is owned exclusively by EF Core
/// migrations — never <c>EnsureCreated</c>, never out-of-band DDL.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>SAMPLE SLICE — drop with the rest of the Item sample.</summary>
    public DbSet<Item> Items => Set<Item>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Every <see cref="DateTime"/>/<see cref="DateTime"/>? column in this schema stores a UTC
    /// instant in a Kind-less SQL Server <c>datetime2</c> — the numeric value round-trips correctly,
    /// but reloading it hands back <see cref="DateTimeKind.Unspecified"/>, not
    /// <see cref="DateTimeKind.Utc"/>. Left uncorrected, System.Text.Json serializes that value
    /// without a trailing <c>Z</c> (it only appends one for <see cref="DateTimeKind.Utc"/>), so a
    /// reloaded entity's timestamp reaches API consumers looking like an unzoned/local value even
    /// though the number itself is UTC. This re-labels Kind on the way in and out globally rather
    /// than hand-patching every property individually. <see cref="DateTime.SpecifyKind"/> — not
    /// <see cref="DateTime.ToUniversalTime"/> — because the numeric value already IS the UTC instant;
    /// ToUniversalTime would (mis)treat an Unspecified value as local and shift it.
    ///
    /// <see cref="DateTimeOffset"/> columns need no such correction: the offset is stored, so they
    /// round-trip unambiguously. Prefer them for new timestamps (the sample <c>Item</c> does).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcValueConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcValueConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>The <see cref="DateTime"/> half of the global UTC-Kind conversion documented on
    /// <see cref="ConfigureConventions"/> — its own named type (rather than a shared
    /// <c>ValueConverter</c> instance) because <c>HaveConversion&lt;TConverter&gt;</c> instantiates
    /// the converter itself via a parameterless constructor.</summary>
    private sealed class UtcValueConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcValueConverter() : base(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    /// <summary>Nullable counterpart of <see cref="UtcValueConverter"/>, for <c>DateTime?</c> columns.</summary>
    private sealed class NullableUtcValueConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcValueConverter() : base(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
        {
        }
    }
}
