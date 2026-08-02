namespace AjBoilerplate.Domain.Common;

/// <summary>
/// The identity performing an action. <paramref name="Role"/> is a plain tag — the Domain never
/// enumerates roles, it only records who acted. <paramref name="Email"/> and
/// <paramref name="Groups"/> are optional identity facets a consuming project can use for
/// ownership/scope checks; both default to absent so a caller that has neither is unaffected.
/// </summary>
public sealed record Actor(
    string Id,
    string Name,
    string Role,
    string? Email = null,
    IReadOnlyList<string>? Groups = null)
{
    /// <summary>The caller's group membership, never null (empty when the token carries no group claim).</summary>
    public IReadOnlyList<string> GroupList => Groups ?? [];
}
