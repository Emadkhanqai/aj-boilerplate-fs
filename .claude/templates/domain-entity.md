# Template: Domain Entity (`AjBoilerplate.Domain`)

Persistence-ignorant. No EF Core or ASP.NET attributes. **Invariants enforced by the type** —
an invalid instance must be unconstructible.

```csharp
namespace AjBoilerplate.Domain.Items;

public sealed class Item
{
    public const int NameMaxLength = 200;

    public int Id { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public ItemStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency token. A stale value yields 409.</summary>
    public byte[] RowVersion { get; private set; } = [];

    private Item() { Name = null!; }   // EF Core

    public Item(string name, string? description, DateTimeOffset now)
    {
        Name = Normalize(name);
        Description = description;
        Status = ItemStatus.Draft;
        CreatedAt = now;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = Normalize(name);
        UpdatedAt = now;
    }

    public void Publish(DateTimeOffset now)
    {
        // Guard the transition here, not in the handler and not in the controller.
        if (Status is not ItemStatus.Draft)
        {
            throw new DomainException(
                DomainErrorCodes.InvalidStateTransition,
                $"Only a Draft item can be published; this one is {Status}.");
        }

        Status = ItemStatus.Published;
        UpdatedAt = now;
    }

    private static string Normalize(string name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException(DomainErrorCodes.Validation, "Name is required.");
        }

        if (trimmed.Length > NameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.Validation,
                $"Name must be {NameMaxLength} characters or fewer.");
        }

        return trimmed;
    }
}

public enum ItemStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2,
}
```

## Rules

- **No `DateTime.UtcNow` inside the domain.** Time is passed in (from `IClock`) so behaviour is
  testable and deterministic.
- **Collections are exposed read-only.** Back them with a private `List<T>` and expose
  `IReadOnlyList<T>`; mutate only through methods that enforce the invariant.
- **No public setters.** State changes go through intention-revealing methods (`Publish`,
  `Rename`) that can guard the transition.
- **`DomainException` carries a stable `code`** which the exception-handler chain maps to a
  status — see [`../standards/error-handling.md`](../standards/error-handling.md).
- Mapping — precision, lengths, indexes, the `rowversion` token — goes in an
  `IEntityTypeConfiguration<Item>` in **Infrastructure**, never here.

See [`../standards/clean-architecture.md`](../standards/clean-architecture.md) and
[`../standards/ef-core.md`](../standards/ef-core.md).
