namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Authorization policy names, enforced server-side. Never trust the client: a hidden button is a
/// UX affordance, not a control.
///
/// Every policy resolves through <c>RoleCapabilities</c> rather than naming roles inline, so adding
/// a role changes one table instead of every policy — see <see cref="AuthenticationSetup"/>.
/// </summary>
public static class Policies
{
    /// <summary>Read ordinary records. The widest policy — every recognised role satisfies it.</summary>
    public const string ReadAccess = "ReadAccess";

    /// <summary>Create, update, or delete ordinary records. Narrower than
    /// <see cref="ReadAccess"/>: a viewer can see everything it grants and change none of it.</summary>
    public const string WriteAccess = "WriteAccess";

    /// <summary>Administrative surfaces (operational dashboards, configuration). Narrowest.</summary>
    public const string AdminAccess = "AdminAccess";
}
