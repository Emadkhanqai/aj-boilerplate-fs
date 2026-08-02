using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Domain.Items;
using AjBoilerplate.UnitTests.Support;
using FluentValidation;

namespace AjBoilerplate.UnitTests.Items;

/// <summary>SAMPLE SLICE. The use-case handler: validation, mapping, and the optimistic-concurrency
/// guard.</summary>
public sealed class ItemServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeItemRepository _repository = new();
    private readonly FixedClock _clock = new(Now);
    private readonly ItemService _service;

    public ItemServiceTests() =>
        _service = new ItemService(
            _repository,
            _clock,
            new CreateItemCommandValidator(),
            new UpdateItemCommandValidator(),
            new ListItemsQueryValidator());

    [Fact]
    public async Task CreateAsync_persists_the_item_and_returns_it()
    {
        var created = await _service.CreateAsync(new CreateItemCommand("Widget", "A widget.", "Active"), CancellationToken.None);

        Assert.Equal("Widget", created.Name);
        Assert.Equal("A widget.", created.Description);
        Assert.Equal(nameof(ItemStatus.Active), created.Status);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Null(created.UpdatedAt);
        Assert.Single(_repository.Items);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task CreateAsync_defaults_an_omitted_status_to_draft()
    {
        var created = await _service.CreateAsync(new CreateItemCommand("Widget", null, null), CancellationToken.None);

        Assert.Equal(nameof(ItemStatus.Draft), created.Status);
    }

    [Fact]
    public async Task CreateAsync_rejects_an_invalid_command_without_persisting()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(new CreateItemCommand("", null, null), CancellationToken.None));

        // The point is not just that it threw — nothing may reach the repository.
        Assert.Empty(_repository.Items);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_id() =>
        Assert.Null(await _service.GetAsync(Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task UpdateAsync_applies_the_change_when_the_token_is_current()
    {
        var seeded = _repository.Seed("Widget", ItemStatus.Draft, Now);
        _clock.Advance(TimeSpan.FromHours(1));

        var updated = await _service.UpdateAsync(
            new UpdateItemCommand(seeded.Id, "Gadget", "Changed.", "Active", seeded.RowVersion),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Gadget", updated.Name);
        Assert.Equal(nameof(ItemStatus.Active), updated.Status);
        Assert.Equal(Now.AddHours(1), updated.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_returns_the_new_token_so_a_second_update_can_succeed()
    {
        var seeded = _repository.Seed("Widget", ItemStatus.Draft, Now);

        var first = await _service.UpdateAsync(
            new UpdateItemCommand(seeded.Id, "Gadget", null, "Active", seeded.RowVersion),
            CancellationToken.None);

        var second = await _service.UpdateAsync(
            new UpdateItemCommand(seeded.Id, "Gizmo", null, "Active", first!.RowVersion),
            CancellationToken.None);

        Assert.Equal("Gizmo", second!.Name);
    }

    [Fact]
    public async Task UpdateAsync_throws_a_stale_conflict_for_an_out_of_date_token()
    {
        var seeded = _repository.Seed("Widget", ItemStatus.Draft, Now);
        var staleToken = Guid.NewGuid().ToByteArray();

        var error = await Assert.ThrowsAsync<StaleItemException>(() => _service.UpdateAsync(
            new UpdateItemCommand(seeded.Id, "Gadget", null, "Active", staleToken),
            CancellationToken.None));

        // StaleItemException derives from ConflictException, which is what maps it to a 409.
        Assert.IsAssignableFrom<ConflictException>(error);
        Assert.Equal("Widget", _repository.Items[0].Name);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_maps_a_lost_database_race_to_the_same_stale_conflict()
    {
        var seeded = _repository.Seed("Widget", ItemStatus.Draft, Now);

        // The caller's token is current, so the service's own pre-check passes — and then another
        // transaction commits first, so SQL Server's `WHERE RowVersion = @original` matches nothing
        // and EF Core throws. The caller must not be able to tell the two cases apart.
        _repository.ThrowConcurrencyOnNextSave = true;

        await Assert.ThrowsAsync<StaleItemException>(() => _service.UpdateAsync(
            new UpdateItemCommand(seeded.Id, "Gadget", null, "Active", seeded.RowVersion),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_returns_null_for_an_unknown_id() =>
        Assert.Null(await _service.UpdateAsync(
            new UpdateItemCommand(Guid.NewGuid(), "Gadget", null, "Active", Guid.NewGuid().ToByteArray()),
            CancellationToken.None));

    [Fact]
    public async Task DeleteAsync_removes_the_item()
    {
        var seeded = _repository.Seed("Widget", ItemStatus.Draft, Now);

        Assert.True(await _service.DeleteAsync(seeded.Id, CancellationToken.None));
        Assert.Empty(_repository.Items);
    }

    [Fact]
    public async Task DeleteAsync_reports_false_for_an_unknown_id() =>
        Assert.False(await _service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task ListAsync_pages_and_reports_the_full_total()
    {
        for (var i = 0; i < 5; i++)
        {
            _repository.Seed($"Widget {i}", ItemStatus.Draft, Now.AddMinutes(i));
        }

        var page = await _service.ListAsync(new ListItemsQuery(1, 2, null), CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        // Total is the count across ALL pages — a client cannot render paging controls otherwise.
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task ListAsync_filters_by_search_term()
    {
        _repository.Seed("Widget", ItemStatus.Draft, Now);
        _repository.Seed("Gadget", ItemStatus.Draft, Now);

        var page = await _service.ListAsync(new ListItemsQuery(1, 20, "gad"), CancellationToken.None);

        Assert.Equal("Gadget", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task ListAsync_rejects_an_out_of_range_page_size() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ListAsync(new ListItemsQuery(1, Paging.MaxPageSize + 1, null), CancellationToken.None));
}
