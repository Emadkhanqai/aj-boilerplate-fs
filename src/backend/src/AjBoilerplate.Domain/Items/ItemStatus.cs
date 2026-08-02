namespace AjBoilerplate.Domain.Items;

/// <summary>
/// Lifecycle of the sample <see cref="Item"/> aggregate. SAMPLE SLICE — delete or rename it on day
/// one along with the rest of <c>Items/</c>.
/// </summary>
public enum ItemStatus
{
    /// <summary>Being worked on; not yet in use.</summary>
    Draft,

    /// <summary>In use.</summary>
    Active,

    /// <summary>Retired. An archived item is read-only — see <see cref="Item.Update"/>.</summary>
    Archived,
}
