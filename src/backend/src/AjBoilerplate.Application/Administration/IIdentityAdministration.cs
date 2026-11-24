namespace AjBoilerplate.Application.Administration;

/// <summary>
/// What the Auth service's internal admin surface answered, folded to what a caller can act on. The
/// fold is for the RESPONSE; the raw upstream status is written to the log by the implementation before
/// it is folded, because the outcomes below each collapse several statuses that have different fixes.
/// </summary>
public enum IdentityAdminOutcome
{
    Success,

    /// <summary>The subject holds no seat the Auth service administers (its own "no seat" 404).</summary>
    UserNotFound,

    /// <summary>A group path is not a managed seat; nothing was changed (upstream 400).</summary>
    GroupNotFound,

    /// <summary>Our credential was refused (upstream 401) or the Auth service's own upstream failed (502):
    /// configuration, never the administrator's permissions.</summary>
    Denied,

    /// <summary>Not configured, unreachable, timed out, a 5xx, or a 404 that was not the "no seat" answer
    /// (the admin surface switched off). Transient or deployment: retry.</summary>
    Unavailable,

    /// <summary>The e-mail's domain may not be given a seat (upstream 422 EMAIL_DOMAIN_NOT_ALLOWED).</summary>
    EmailDomainNotAllowed,

    /// <summary>The key was accepted and the Auth service refused the portal (upstream 403): a scope problem
    /// with this installation's key, not an empty answer.</summary>
    PortalNotPermitted,

    /// <summary>Upstream 501: it has no equivalent of this operation.</summary>
    NotSupportedByUpstream,

    /// <summary>The Auth service rejected the call on its own terms and a retry will not help (upstream 409:
    /// more than one account carries that address). A person has to resolve it.</summary>
    UpstreamRejected,
}

public sealed record PortalAccess(string Path, IdentityAdminOutcome Outcome);

public sealed record NewDirectoryUser(string Email, string FirstName, string LastName, string GroupPath);

public sealed record DirectoryUser(
    string Subject, string Username, string? Email, string? FirstName, string? LastName, bool Enabled);

public sealed record DirectoryGroup(string Path, string Name, string? ParentPath);

public sealed record DirectoryMember(DirectoryUser User, IReadOnlyList<string> Groups);

/// <summary>The outcome of a subject-keyed call, with what a (possibly partial) write changed. A failed write
/// is not necessarily a write that did nothing: the upstream issues adds before removes and rolls nothing
/// back, so <see cref="Added"/> and <see cref="Removed"/> are carried on a failure too.</summary>
public sealed record IdentityAdminResult(
    IdentityAdminOutcome Outcome,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<PortalAccess>? Portals = null,
    DirectoryUser? User = null)
{
    public static IdentityAdminResult Failed(IdentityAdminOutcome outcome) => new(outcome, [], [], []);

    public bool Succeeded => Outcome == IdentityAdminOutcome.Success;
}

/// <summary>Port to the Auth service's <c>/internal/admin/*</c>. Implemented in Infrastructure; the API never
/// holds the admin key.</summary>
public interface IIdentityAdministration
{
    Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryUser> Users)> SearchUsersAsync(
        string term, int limit, CancellationToken cancellationToken);

    Task<IdentityAdminResult> CreateUserAsync(NewDirectoryUser request, CancellationToken cancellationToken);

    Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryGroup> Groups, IReadOnlyList<PortalAccess> Portals)>
        GetGroupsCatalogAsync(CancellationToken cancellationToken);

    Task<(IdentityAdminOutcome Outcome, IReadOnlyList<DirectoryMember> Members, IReadOnlyList<PortalAccess> Portals)>
        GetMembersAsync(CancellationToken cancellationToken);

    Task<IdentityAdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken);

    Task<IdentityAdminResult> SetGroupsAsync(
        string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken);

    Task<IdentityAdminResult> DisableAsync(string subject, CancellationToken cancellationToken);

    Task<IdentityAdminResult> EnableAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Flags every live session of the person for a re-exchange, so a changed seat takes effect
    /// without waiting for the next token renewal.</summary>
    Task<IdentityAdminOutcome> RefreshSessionsAsync(string subject, CancellationToken cancellationToken);
}
