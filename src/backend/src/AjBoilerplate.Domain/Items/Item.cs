using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.Items;

/// <summary>
/// THE SAMPLE SLICE. <c>Item</c> exists only to prove the Domain → Application → Contracts →
/// Infrastructure → Api → OpenAPI path end to end, and to give the EF Core migration workflow one
/// real table to demonstrate. It carries no business meaning: delete the whole <c>Items/</c> folder
/// in every layer (plus <c>ItemsController</c>, the <c>InitialCreate</c> migration, and the Items
/// tests) on day one, or rename it into your first real aggregate.
///
/// It is a deliberately complete example of the house style rather than a stub: a private
/// constructor plus a validating factory, invariants enforced in the domain (never in the
/// controller), all mutation through behaviour methods, and an optimistic concurrency token rolled
/// forward by <see cref="AuditedEntity.Touch"/> on every change.
/// </summary>
public sealed class Item : AuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>; mirrored by the EF configuration and the
    /// request validator so all three can never drift.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 2000;

    private Item()
    {
        Name = string.Empty;
    }

    private Item(Guid id, string name, string? description, ItemStatus status, DateTimeOffset nowUtc)
    {
        Id = id;
        Name = name;
        Description = description;
        Status = status;
        MarkCreated(nowUtc);
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public ItemStatus Status { get; private set; }

    /// <summary>
    /// Creates a new item. <paramref name="name"/> is required and bounded;
    /// <paramref name="description"/> is optional and bounded. Whitespace is trimmed here so two
    /// callers cannot create "the same" item that differs only by padding.
    /// </summary>
    public static Item Create(Guid id, string name, string? description, ItemStatus status, DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An item's id is required.");
        }

        return new Item(id, NormalizeName(name), NormalizeDescription(description), status, nowUtc);
    }

    /// <summary>
    /// Replaces the item's name, description, and status. Archived items are read-only: reactivating
    /// one is a deliberate, separate decision (<see cref="Restore"/>), never a side effect of an
    /// ordinary edit that happened to send a different status.
    /// </summary>
    public void Update(string name, string? description, ItemStatus status, DateTimeOffset nowUtc)
    {
        if (Status == ItemStatus.Archived)
        {
            throw new DomainException("An archived item cannot be edited. Restore it first.");
        }

        Name = NormalizeName(name);
        Description = NormalizeDescription(description);
        Status = status;
        Touch(nowUtc);
    }

    /// <summary>Archives the item. Idempotent — archiving an already-archived item is a no-op rather
    /// than an error, so a retried request cannot fail for having succeeded.</summary>
    public void Archive(DateTimeOffset nowUtc)
    {
        if (Status == ItemStatus.Archived)
        {
            return;
        }

        Status = ItemStatus.Archived;
        Touch(nowUtc);
    }

    /// <summary>Brings an archived item back to <see cref="ItemStatus.Draft"/>.</summary>
    public void Restore(DateTimeOffset nowUtc)
    {
        if (Status != ItemStatus.Archived)
        {
            throw new DomainException("Only an archived item can be restored.");
        }

        Status = ItemStatus.Draft;
        Touch(nowUtc);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("An item's name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException($"An item's name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new DomainException($"An item's description cannot exceed {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }
}
