namespace AjBoilerplate.Application.Common;

/// <summary>
/// One page of an Application-layer query result. Deliberately NOT
/// <c>AjBoilerplate.Contracts.PagedResponse&lt;T&gt;</c>: the Application layer must not depend on
/// the wire contract, so the Api layer maps between the two. They look alike today and are free to
/// diverge tomorrow.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
