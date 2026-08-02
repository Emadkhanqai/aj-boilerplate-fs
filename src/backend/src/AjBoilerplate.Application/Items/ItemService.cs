using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Common;
using AjBoilerplate.Domain.Items;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Application.Items;

/// <summary>SAMPLE SLICE. The item use cases. Controllers call this and nothing else.</summary>
public interface IItemService
{
    Task<PagedResult<ItemDto>> ListAsync(ListItemsQuery query, CancellationToken cancellationToken);

    /// <summary>The item, or null when no item has that id — a foreseeable outcome the caller turns
    /// into a 404, not an exception.</summary>
    Task<ItemDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ItemDto> CreateAsync(CreateItemCommand command, CancellationToken cancellationToken);

    /// <summary>The updated item, or null when no item has that id. Throws
    /// <see cref="StaleItemException"/> when the caller's concurrency token is out of date.</summary>
    Task<ItemDto?> UpdateAsync(UpdateItemCommand command, CancellationToken cancellationToken);

    /// <summary>True when an item was deleted, false when none had that id.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IItemService"/>
public sealed class ItemService : IItemService
{
    private readonly IItemRepository _items;
    private readonly IClock _clock;
    private readonly IValidator<CreateItemCommand> _createValidator;
    private readonly IValidator<UpdateItemCommand> _updateValidator;
    private readonly IValidator<ListItemsQuery> _listValidator;

    public ItemService(
        IItemRepository items,
        IClock clock,
        IValidator<CreateItemCommand> createValidator,
        IValidator<UpdateItemCommand> updateValidator,
        IValidator<ListItemsQuery> listValidator)
    {
        _items = items;
        _clock = clock;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _listValidator = listValidator;
    }

    public async Task<PagedResult<ItemDto>> ListAsync(ListItemsQuery query, CancellationToken cancellationToken)
    {
        await _listValidator.ValidateAndThrowAsync(query, cancellationToken);

        var page = await _items.ListAsync(query.Page, query.PageSize, Normalize(query.Search), cancellationToken);
        return new PagedResult<ItemDto>(
            page.Items.Select(ToDto).ToList(),
            page.Total,
            page.Page,
            page.PageSize);
    }

    public async Task<ItemDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await _items.FindByIdAsync(id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    public async Task<ItemDto> CreateAsync(CreateItemCommand command, CancellationToken cancellationToken)
    {
        await _createValidator.ValidateAndThrowAsync(command, cancellationToken);

        var item = Item.Create(
            Guid.NewGuid(), command.Name, command.Description, ItemStatusNames.Parse(command.Status), _clock.UtcNowOffset);
        await _items.AddAsync(item, cancellationToken);
        await _items.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<ItemDto?> UpdateAsync(UpdateItemCommand command, CancellationToken cancellationToken)
    {
        await _updateValidator.ValidateAndThrowAsync(command, cancellationToken);

        var item = await _items.FindByIdAsync(command.Id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        // Two guards, deliberately, and only the second one is authoritative.
        //
        // This first check is a courtesy: the caller is holding a token from before someone else's
        // save, and comparing it here fails fast with a clear 409 before any work is done. It cannot
        // be the real guard, because between this comparison and the save below another transaction
        // may still commit.
        if (!item.RowVersion.AsSpan().SequenceEqual(command.RowVersion))
        {
            throw new StaleItemException(command.Id);
        }

        item.Update(command.Name, command.Description, ItemStatusNames.Parse(command.Status), _clock.UtcNowOffset);

        try
        {
            await _items.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // This is the authoritative guard, and it closes the window the check above leaves open.
            // EF Core emits `UPDATE ... WHERE Id = @id AND RowVersion = @original`, so if anything
            // committed since the read, SQL Server itself matches zero rows and EF raises this.
            // Same outcome for the caller either way — a 409, never a silent overwrite.
            throw new StaleItemException(command.Id);
        }

        return ToDto(item);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await _items.FindByIdAsync(id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        _items.Remove(item);
        await _items.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Normalize(string? search)
    {
        var trimmed = search?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static ItemDto ToDto(Item item) => new(
        item.Id,
        item.Name,
        item.Description,
        item.Status.ToString(),
        item.CreatedAt,
        item.UpdatedAt,
        item.RowVersion);
}
