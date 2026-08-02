namespace AjBoilerplate.Application.Common;

/// <summary>Shared paging bounds, so no list endpoint can accidentally return an unbounded result
/// set and every validator agrees on the same limits.</summary>
public static class Paging
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 20;

    /// <summary>An upper bound, not a recommendation. A caller asking for more is a validation
    /// error, never a silently-clamped success — a clamped page size makes a client's paging maths
    /// wrong without telling it.</summary>
    public const int MaxPageSize = 100;
}
