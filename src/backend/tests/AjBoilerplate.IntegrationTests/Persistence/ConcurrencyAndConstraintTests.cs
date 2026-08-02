using AjBoilerplate.Application.Persistence;
using AjBoilerplate.Domain.Items;
using AjBoilerplate.Domain.Messaging.Inbox;
using AjBoilerplate.Infrastructure.Persistence;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AjBoilerplate.IntegrationTests.Persistence;

/// <summary>
/// The behaviour that only a real database can demonstrate: a store-issued <c>rowversion</c>, a
/// unique index that actually rejects a duplicate, and the SQL Server error-number mapping in
/// <see cref="SqlUniqueConstraintViolation"/> / <see cref="SqlDeadlockVictim"/>.
///
/// None of this is reachable with the EF Core in-memory provider — it issues no rowversion, enforces
/// no unique index, and never produces a <c>SqlException</c> — which is precisely why this suite runs
/// against a containerised SQL Server.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConcurrencyAndConstraintTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ConcurrencyAndConstraintTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_database_issues_a_row_version_on_insert()
    {
        var id = Guid.NewGuid();

        await _fixture.WithDbContextAsync(async db =>
        {
            var item = Item.Create(id, $"Issued {id:N}", null, ItemStatus.Draft, Now);
            // Nothing assigns this in application code — if it is non-empty afterwards, SQL Server
            // populated it.
            Assert.Empty(item.RowVersion);

            db.Items.Add(item);
            await db.SaveChangesAsync(CancellationToken.None);

            Assert.NotEmpty(item.RowVersion);
            // SQL Server's rowversion is always 8 bytes.
            Assert.Equal(8, item.RowVersion.Length);
        });
    }

    [Fact]
    public async Task The_database_advances_the_row_version_on_update()
    {
        var id = Guid.NewGuid();
        byte[] afterInsert = [];

        await _fixture.WithDbContextAsync(async db =>
        {
            var item = Item.Create(id, $"Advancing {id:N}", null, ItemStatus.Draft, Now);
            db.Items.Add(item);
            await db.SaveChangesAsync(CancellationToken.None);
            afterInsert = item.RowVersion;
        });

        await _fixture.WithDbContextAsync(async db =>
        {
            var item = await db.Items.SingleAsync(i => i.Id == id, CancellationToken.None);
            item.Update("Advanced", null, ItemStatus.Active, Now.AddHours(1));
            await db.SaveChangesAsync(CancellationToken.None);

            Assert.NotEqual(afterInsert, item.RowVersion);
        });
    }

    [Fact]
    public async Task A_concurrent_update_loses_with_a_concurrency_exception()
    {
        var id = Guid.NewGuid();

        await _fixture.WithDbContextAsync(async db =>
        {
            db.Items.Add(Item.Create(id, $"Contended {id:N}", null, ItemStatus.Draft, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        // Two independent contexts load the same row — the real shape of two requests in flight at
        // once. Neither has any knowledge of the other.
        await using var firstScope = _fixture.Api.Services.CreateAsyncScope();
        await using var secondScope = _fixture.Api.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var firstItem = await first.Items.SingleAsync(i => i.Id == id, CancellationToken.None);
        var secondItem = await second.Items.SingleAsync(i => i.Id == id, CancellationToken.None);

        firstItem.Update("First writer", null, ItemStatus.Active, Now.AddHours(1));
        await first.SaveChangesAsync(CancellationToken.None);

        secondItem.Update("Second writer", null, ItemStatus.Active, Now.AddHours(2));

        // The second UPDATE carries `WHERE RowVersion = @original`, which no longer matches, so SQL
        // Server affects zero rows and EF Core raises this. Without a working rowversion the second
        // write would silently win and the first writer's change would vanish.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(CancellationToken.None));

        await _fixture.WithDbContextAsync(async db =>
            Assert.Equal("First writer", (await db.Items.SingleAsync(i => i.Id == id, CancellationToken.None)).Name));
    }

    [Fact]
    public async Task A_duplicate_source_event_id_trips_the_unique_index_and_is_classified_correctly()
    {
        var sourceEventId = $"evt-{Guid.NewGuid():N}";

        await _fixture.WithDbContextAsync(async db =>
        {
            db.InboxMessages.Add(InboxMessage.Create(sourceEventId, "SomethingHappened", "{}", Now.UtcDateTime));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        DbUpdateException? caught = null;
        await _fixture.WithDbContextAsync(async db =>
        {
            // A genuine redelivery of the same event. The application-level "have I seen this?" lookup
            // cannot be the guarantee — two concurrent replays can both pass it — so the unique index
            // is the real guard, and this is what it feels like when it fires.
            db.InboxMessages.Add(InboxMessage.Create(sourceEventId, "SomethingHappened", "{}", Now.UtcDateTime));
            caught = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
        });

        Assert.NotNull(caught);

        // The classifier is what turns this into an idempotent success or a clean 409 instead of a
        // 500. Asserting it against a REAL SqlException is the only way to know the error numbers
        // it looks for are the ones SQL Server actually raises.
        Assert.True(
            SqlUniqueConstraintViolation.Matches(caught),
            "A duplicate-key failure must be recognised as a unique-constraint violation.");

        // ...and it must stay narrow. A unique-constraint violation is not a deadlock, and a
        // classifier that said otherwise would make the caller retry a write that can never succeed.
        Assert.False(
            SqlDeadlockVictim.Matches(caught),
            "A unique-constraint violation must not be misclassified as a deadlock.");
    }
}
