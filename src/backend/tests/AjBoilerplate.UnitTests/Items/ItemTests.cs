using AjBoilerplate.Domain.Common;
using AjBoilerplate.Domain.Items;

namespace AjBoilerplate.UnitTests.Items;

/// <summary>SAMPLE SLICE. The domain rules on <see cref="Item"/>, asserted directly — no
/// persistence, no HTTP, no mocks.</summary>
public sealed class ItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_whitespace_from_name_and_description()
    {
        var item = Item.Create(Guid.NewGuid(), "  Widget  ", "  A widget.  ", ItemStatus.Draft, Now);

        Assert.Equal("Widget", item.Name);
        Assert.Equal("A widget.", item.Description);
    }

    [Fact]
    public void Create_treats_a_blank_description_as_absent() =>
        // Otherwise "" and null would both be storable and mean the same thing, and every consumer
        // would have to check for both.
        Assert.Null(Item.Create(Guid.NewGuid(), "Widget", "   ", ItemStatus.Draft, Now).Description);

    [Fact]
    public void Create_stamps_created_at_and_leaves_updated_at_absent()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Draft, Now);

        Assert.Equal(Now, item.CreatedAt);
        Assert.Null(item.UpdatedAt);
    }

    [Fact]
    public void Create_leaves_the_concurrency_token_for_the_database_to_issue() =>
        // RowVersion is a SQL Server rowversion: the engine issues it on INSERT. Nothing in the
        // domain may assign one, because a token invented here would replace the loaded original
        // that EF Core needs for the concurrency predicate and silently disable the check.
        // ConcurrencyAndConstraintTests proves the database does issue and advance it.
        Assert.Empty(Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Draft, Now).RowVersion);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string name) =>
        Assert.Throws<DomainException>(() => Item.Create(Guid.NewGuid(), name, null, ItemStatus.Draft, Now));

    [Fact]
    public void Create_rejects_a_name_over_the_limit() =>
        Assert.Throws<DomainException>(() =>
            Item.Create(Guid.NewGuid(), new string('x', Item.MaxNameLength + 1), null, ItemStatus.Draft, Now));

    [Fact]
    public void Create_accepts_a_name_exactly_at_the_limit() =>
        // The boundary itself is valid — an off-by-one here would silently reject legitimate input.
        Assert.Equal(
            Item.MaxNameLength,
            Item.Create(Guid.NewGuid(), new string('x', Item.MaxNameLength), null, ItemStatus.Draft, Now).Name.Length);

    [Fact]
    public void Create_rejects_a_description_over_the_limit() =>
        Assert.Throws<DomainException>(() =>
            Item.Create(Guid.NewGuid(), "Widget", new string('x', Item.MaxDescriptionLength + 1), ItemStatus.Draft, Now));

    [Fact]
    public void Create_rejects_an_empty_id() =>
        Assert.Throws<DomainException>(() => Item.Create(Guid.Empty, "Widget", null, ItemStatus.Draft, Now));

    [Fact]
    public void Update_applies_the_new_values_and_stamps_updated_at()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Draft, Now);
        var later = Now.AddHours(1);

        item.Update("Gadget", "Now a gadget.", ItemStatus.Active, later);

        Assert.Equal("Gadget", item.Name);
        Assert.Equal("Now a gadget.", item.Description);
        Assert.Equal(ItemStatus.Active, item.Status);
        Assert.Equal(later, item.UpdatedAt);
    }

    [Fact]
    public void Update_does_not_assign_a_concurrency_token()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Draft, Now);

        item.Update("Gadget", null, ItemStatus.Active, Now.AddHours(1));

        // Advancing the token is the database's job, on the UPDATE this change produces. Assigning
        // one here would overwrite EF Core's loaded original and turn the concurrency check into a
        // no-op that still looks like it is working.
        Assert.Empty(item.RowVersion);
    }

    [Fact]
    public void Update_rejects_an_archived_item()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Draft, Now);
        item.Archive(Now);

        var error = Assert.Throws<DomainException>(() => item.Update("Gadget", null, ItemStatus.Active, Now));
        Assert.Contains("archived", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Archive_is_idempotent()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Active, Now);
        item.Archive(Now.AddHours(1));

        item.Archive(Now.AddHours(2));

        // A retried request must not fail, and must not look like a change either — so the second
        // call leaves UpdatedAt at the first archive's timestamp rather than re-stamping it.
        Assert.Equal(ItemStatus.Archived, item.Status);
        Assert.Equal(Now.AddHours(1), item.UpdatedAt);
    }

    [Fact]
    public void Restore_returns_an_archived_item_to_draft()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Active, Now);
        item.Archive(Now);

        item.Restore(Now.AddHours(1));

        Assert.Equal(ItemStatus.Draft, item.Status);
    }

    [Fact]
    public void Restore_rejects_an_item_that_is_not_archived()
    {
        var item = Item.Create(Guid.NewGuid(), "Widget", null, ItemStatus.Active, Now);

        Assert.Throws<DomainException>(() => item.Restore(Now));
    }
}
