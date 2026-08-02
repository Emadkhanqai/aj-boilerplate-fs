namespace AjBoilerplate.Contracts.Common;

/// <summary>
/// A single page of results. <paramref name="Total"/> is the full count across all pages so a
/// client can render paging controls; <paramref name="Items"/> is only the current page.
/// </summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
