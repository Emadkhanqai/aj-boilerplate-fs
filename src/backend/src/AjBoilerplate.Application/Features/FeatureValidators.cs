using FluentValidation;

namespace AjBoilerplate.Application.Features;

/// <summary>
/// Bounds the path a caller may ask about. The value is never used to build SQL or to reach the file
/// system — it is canonicalised and prefix-compared in memory — so the only risk it carries is size,
/// and this is what bounds it.
/// </summary>
public sealed class UnacknowledgedFeaturesQueryValidator : AbstractValidator<UnacknowledgedFeaturesQuery>
{
    /// <summary>Comfortably above the longest URL any mainstream browser will issue.</summary>
    public const int MaxPathLength = 2048;

    public UnacknowledgedFeaturesQueryValidator() =>
        RuleFor(q => q.Path)
            .MaximumLength(MaxPathLength)
            .WithMessage($"Path cannot exceed {MaxPathLength} characters.");
}

/// <summary>
/// Validates a dismissal. An EMPTY list is deliberately valid — dismissing nothing is a successful
/// no-op, not a client error — but a null list, an empty id, or an unbounded batch is rejected
/// before any work is done.
/// </summary>
public sealed class AcknowledgeFeaturesCommandValidator : AbstractValidator<AcknowledgeFeaturesCommand>
{
    public AcknowledgeFeaturesCommandValidator()
    {
        RuleFor(c => c.FeatureIds)
            .NotNull().WithMessage("FeatureIds is required.")
            .Must(ids => ids is null || ids.Count <= AcknowledgeFeaturesCommand.MaxFeatureIds)
            .WithMessage($"FeatureIds cannot contain more than {AcknowledgeFeaturesCommand.MaxFeatureIds} entries.");

        RuleForEach(c => c.FeatureIds)
            .NotEmpty().WithMessage("FeatureIds cannot contain an empty id.");
    }
}
