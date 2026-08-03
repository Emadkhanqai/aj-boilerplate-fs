namespace AjBoilerplate.Application.Identity;

/// <summary>
/// Well-known actor identifiers. These are the values <c>IActorClaims.Id</c> reports when there is no
/// real authenticated subject to report, and they live here — one layer both the Api's claims reader
/// and the Application's use cases can see — so a check for "is this a real user?" cannot drift from
/// what the claims reader actually produces.
/// </summary>
public static class ActorIdentifiers
{
    /// <summary>Reported when the request carries no authenticated principal at all.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Reported by the placeholder actor used where no authentication pipeline is wired
    /// up (a console tool, a design-time host).</summary>
    public const string System = "system";

    /// <summary>
    /// True when <paramref name="actorId"/> does not identify a real authenticated person. A use case
    /// that records something PER USER must refuse these rather than write rows attributed to a
    /// shared pseudo-identity that every unauthenticated caller would share.
    /// </summary>
    public static bool IsRealUser(string? actorId)
    {
        var trimmed = actorId?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && !string.Equals(trimmed, Anonymous, StringComparison.Ordinal)
            && !string.Equals(trimmed, System, StringComparison.Ordinal);
    }
}
