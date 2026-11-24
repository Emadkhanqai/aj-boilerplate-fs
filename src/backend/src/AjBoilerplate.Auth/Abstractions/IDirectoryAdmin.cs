namespace AjBoilerplate.Auth.Abstractions;

/// <summary>What a directory operation concluded. The endpoint maps each to a status; a member added later defaults to a refusal.</summary>
public enum AdminOutcome
{
    Success,

    /// <summary>The subject is not in the directory.</summary>
    UserNotFound,

    /// <summary>A requested group path is not a seat under a managed path (or does not exist). Nothing was changed.</summary>
    GroupNotFound,

    /// <summary>The upstream refused OUR credential (not the request): the admin client is wrong, expired or lacks a realm-management role.</summary>
    Denied,

    /// <summary>Network, timeout, 5xx or throttling: retryable, and the caller is told so.</summary>
    Transient,

    /// <summary>Every managed portal refused the caller (the upstream credential is not scoped to those groups).</summary>
    PortalNotPermitted,

    /// <summary>The configured upstream has no equivalent of the operation. Not a failure and not retryable.</summary>
    NotSupportedByUpstream,

    /// <summary>The upstream rejected the call on its own terms and a retry cannot help (invalid request, or a duplicate account).</summary>
    UpstreamRejected,

    /// <summary>The e-mail's domain is not allowed to receive a new account (deny by default). Nothing reached the directory.</summary>
    EmailDomainNotAllowed,
}

/// <summary>A directory account. <paramref name="Subject"/> is the id Keycloak puts in <c>sub</c>.</summary>
public sealed record RealmUser(
    string Subject,
    string Username,
    string? Email,
    string? FirstName,
    string? LastName,
    bool Enabled);

/// <summary>A group in the realm: <paramref name="Path"/> is the full path (<c>/my-app/editors</c>), <paramref name="ParentPath"/> its parent.</summary>
public sealed record RealmGroup(string Path, string Name, string? ParentPath);

/// <summary>A person holding at least one seat, with the seats (full group paths) they hold under the managed paths.</summary>
public sealed record PortalMember(RealmUser User, IReadOnlyList<string> Groups);

/// <summary>
/// Whether one managed path could be read. A listing is trustworthy only for the paths whose outcome is <c>Success</c>: anything else
/// is UNKNOWN for that path, never "empty".
/// </summary>
public sealed record PortalAccess(string Path, AdminOutcome Outcome)
{
    public bool Readable => Outcome == AdminOutcome.Success;
}

public enum MembershipChange
{
    Add,
    Remove,
}

/// <summary>A membership change that was asked for and NOT issued, because its managed path could not be read.</summary>
public sealed record WithheldChange(string Path, string? Portal, AdminOutcome Outcome, MembershipChange Requested);

public sealed record MembersView(
    AdminOutcome Outcome,
    IReadOnlyList<PortalMember> Members,
    IReadOnlyList<PortalAccess> Portals);

public sealed record GroupTreeView(
    AdminOutcome Outcome,
    IReadOnlyList<RealmGroup> Groups,
    IReadOnlyList<PortalAccess> Portals);

/// <summary>A grant for a person who may have no account yet: keyed by e-mail, because such a person has no subject.</summary>
public sealed record NewUser(string Email, string FirstName, string LastName, string GroupPath);

/// <summary>The result of a read or write. Fields beyond <see cref="Outcome"/> are set where they apply.</summary>
public sealed record AdminResult(
    AdminOutcome Outcome,
    IReadOnlyCollection<string>? Groups = null,
    IReadOnlyCollection<string>? Added = null,
    IReadOnlyCollection<string>? Removed = null,
    IReadOnlyList<PortalAccess>? Portals = null,
    RealmUser? User = null,
    bool? Created = null,
    IReadOnlyList<WithheldChange>? Withheld = null)
{
    public static AdminResult UserNotFound { get; } = new(AdminOutcome.UserNotFound);

    public static AdminResult GroupNotFound { get; } = new(AdminOutcome.GroupNotFound);

    public static AdminResult Denied { get; } = new(AdminOutcome.Denied);

    public static AdminResult Transient { get; } = new(AdminOutcome.Transient);

    public static AdminResult NotSupported { get; } = new(AdminOutcome.NotSupportedByUpstream);

    public static AdminResult Failed(AdminOutcome outcome) => new(outcome);

    /// <summary>
    /// A failure half-way through a multi-call change, carrying what the upstream CONFIRMED. A failure that reported nothing would be
    /// a write this service performed and denied performing.
    /// </summary>
    public static AdminResult PartiallyApplied(
        AdminOutcome outcome,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> removed,
        IReadOnlyList<WithheldChange> withheld) =>
        new(outcome, Groups: groups, Added: added, Removed: removed, Withheld: withheld);

    public bool Succeeded => Outcome == AdminOutcome.Success;
}

/// <summary>
/// The directory behind <c>/internal/admin</c>: who holds which seat, and changing it. This is a PORT: the shipped implementation is
/// <c>KeycloakAdminClient</c> (the Keycloak Admin REST API); a project whose directory sits behind another interface (an API gateway,
/// say) replaces that one class and nothing else changes. <see cref="NotConfiguredDirectoryAdmin"/> answers every call
/// <see cref="AdminOutcome.NotSupportedByUpstream"/> until a directory credential is configured.
/// </summary>
/// <remarks>
/// Every method is keyed by the user's <c>sub</c> on the read side. Membership is declarative: the caller states the seats it wants and
/// a repeat of the same request changes nothing, which keeps a retry after a timeout safe. Only groups UNDER a managed path
/// (<c>Auth:Admin:ManagedGroupPaths</c>) are ever read or written.
/// </remarks>
public interface IDirectoryAdmin
{
    /// <summary>Finds accounts by a free-text term (name, username or e-mail).</summary>
    Task<(AdminOutcome Outcome, IReadOnlyList<RealmUser> Users)> SearchUsersAsync(string term, int limit, CancellationToken cancellationToken);

    /// <summary>The managed paths and the seats under them, so a screen offers the real choices.</summary>
    Task<GroupTreeView> GetGroupTreeAsync(CancellationToken cancellationToken);

    /// <summary>Everyone who holds a seat under a managed path, with those seats.</summary>
    Task<MembersView> GetPortalMembersAsync(CancellationToken cancellationToken);

    /// <summary>The seats one user holds under the managed paths.</summary>
    Task<AdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Makes the user's seats under the managed paths exactly <paramref name="groupPaths"/>. Every path must be a seat, or nothing changes.</summary>
    Task<AdminResult> SetGroupsAsync(string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken);

    /// <summary>Puts a person in a seat, creating the account if the realm has none for that e-mail.</summary>
    Task<AdminResult> CreateUserAsync(NewUser request, CancellationToken cancellationToken);

    /// <summary>Enables or disables the account.</summary>
    Task<AdminResult> SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken);
}
