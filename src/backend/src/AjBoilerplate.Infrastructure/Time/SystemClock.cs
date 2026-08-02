using AjBoilerplate.Application.Abstractions;

namespace AjBoilerplate.Infrastructure.Time;

/// <summary>The real clock. The only place in the codebase that reads the machine's wall clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow;
}
