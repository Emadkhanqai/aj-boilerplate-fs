using AjBoilerplate.Application.Common;
using AjBoilerplate.Domain.Items;
using FluentValidation;

namespace AjBoilerplate.Application.Items;

/// <summary>
/// SAMPLE SLICE. Request validation lives on the COMMAND, in the Application layer — not on the wire
/// DTO in the Api layer — so every caller of the use case is validated, including one that never
/// came in over HTTP. Lengths come from the domain constants so the validator, the entity, and the
/// EF configuration cannot drift.
/// </summary>
public sealed class CreateItemCommandValidator : AbstractValidator<CreateItemCommand>
{
    public CreateItemCommandValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(Item.MaxNameLength);

        RuleFor(c => c.Description)
            .MaximumLength(Item.MaxDescriptionLength);

        // Optional on create — blank means Draft. A PRESENT but unrecognised value is still a 400,
        // never a silent fall back to the default.
        RuleFor(c => c.Status)
            .Must(status => string.IsNullOrWhiteSpace(status) || ItemStatusNames.IsKnown(status))
            .WithMessage($"Status must be one of: {string.Join(", ", ItemStatusNames.All)}.");
    }
}

/// <summary>Same rules as <see cref="CreateItemCommandValidator"/>, plus the identity, the required
/// status, and the concurrency token an update must carry.</summary>
public sealed class UpdateItemCommandValidator : AbstractValidator<UpdateItemCommand>
{
    public UpdateItemCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(c => c.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(Item.MaxNameLength);

        RuleFor(c => c.Description)
            .MaximumLength(Item.MaxDescriptionLength);

        RuleFor(c => c.Status)
            .NotEmpty().WithMessage("Status is required.")
            .Must(ItemStatusNames.IsKnown)
            .WithMessage($"Status must be one of: {string.Join(", ", ItemStatusNames.All)}.");

        // A missing or malformed token is a CLIENT bug, not a lost race: reject it as a 400 here
        // rather than letting it reach the comparison, where it would masquerade as a 409 and tell
        // the user someone else had edited the record.
        RuleFor(c => c.RowVersion)
            .NotNull().WithMessage("RowVersion is required.")
            .Must(v => v is { Length: > 0 }).WithMessage("RowVersion is required.");
    }
}

/// <summary>Bounds the list query so no caller can request an unbounded page.</summary>
public sealed class ListItemsQueryValidator : AbstractValidator<ListItemsQuery>
{
    public ListItemsQueryValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Page must be 1 or greater.");

        RuleFor(q => q.PageSize)
            .GreaterThanOrEqualTo(1).WithMessage("PageSize must be 1 or greater.")
            .LessThanOrEqualTo(Paging.MaxPageSize)
            .WithMessage($"PageSize cannot exceed {Paging.MaxPageSize}.");

        RuleFor(q => q.Search)
            .MaximumLength(Item.MaxNameLength);
    }
}
