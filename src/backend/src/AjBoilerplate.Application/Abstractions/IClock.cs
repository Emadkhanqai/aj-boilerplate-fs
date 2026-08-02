namespace AjBoilerplate.Application.Abstractions;

/// <summary>Abstraction over the system clock so time is testable. Implemented in Infrastructure.
/// Nothing outside Infrastructure may call <see cref="DateTime.UtcNow"/> directly.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>The same instant as <see cref="UtcNow"/>, with an explicit zero offset — the shape
    /// entities storing <see cref="DateTimeOffset"/> timestamps consume.</summary>
    DateTimeOffset UtcNowOffset { get; }
}
