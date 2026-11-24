using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// The write half of role management: change the user in Keycloak, then make every live session of
/// that user notice.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the Keycloak-events path optional rather than load-bearing. When the change comes through this surface we
/// are the writer, so there is nothing to publish and nothing to wait for: the session flags are set in the same request that made
/// the change. <c>POST /auth/events</c> matters only for changes made directly in the Keycloak console, behind our back.
/// </para>
/// <para>
/// Order is deliberate. Keycloak first, sessions second: a flagged session whose Keycloak write
/// failed would refresh, get the SAME token back, and quietly look successful. Flagging after the
/// write means the worst case is a change that is live in Keycloak but converges on the user's next
/// request instead of this one — the same convergence the event path gives.
/// </para>
/// </remarks>
public sealed partial class UserAdminService
{
    /// <summary>The surface named in the creation-refusal log lines, as a field and not template text.</summary>
    private const string CreateSurface = "POST /internal/admin/users";

    private readonly IDirectoryAdmin _directory;
    private readonly ISessionStore _sessions;
    private readonly ForceRefreshHandler _roleChange;
    private readonly IOptions<AuthOptions> _options;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        IDirectoryAdmin directory,
        ISessionStore sessions,
        ForceRefreshHandler roleChange,
        IOptions<AuthOptions> options,
        ILogger<UserAdminService> logger)
    {
        _options = options;
        _directory = directory;
        _sessions = sessions;
        _roleChange = roleChange;
        _logger = logger;
    }

    /// <summary>Finds users for an administration screen.</summary>
    public Task<(AdminOutcome Outcome, IReadOnlyList<RealmUser> Users)> SearchUsersAsync(
        string term, int limit, CancellationToken cancellationToken) =>
        _directory.SearchUsersAsync(term, limit, cancellationToken);

    /// <summary>Everyone who holds a seat in one of the managed products, with those seats.</summary>
    public Task<MembersView> GetPortalMembersAsync(CancellationToken cancellationToken) =>
        _directory.GetPortalMembersAsync(cancellationToken);

    /// <summary>The managed portals and their seats, so a screen offers the real choices.</summary>
    public Task<GroupTreeView> GetGroupTreeAsync(CancellationToken cancellationToken) =>
        _directory.GetGroupTreeAsync(cancellationToken);

    /// <summary>
    /// Puts a person in a seat, creating the account if the realm does not have one, then flags any
    /// live sessions they already have.
    /// </summary>
    /// <remarks>
    /// The flag is not redundant for a NEW account — the same e-mail can already hold a session
    /// through another portal, and this grant is exactly the change that session needs to notice.
    /// Same order as <see cref="SetGroupsAsync"/>: upstream first, sessions second.
    /// </remarks>
    public async Task<AdminResult> CreateUserAsync(NewUser request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // THE E-MAIL DOMAIN RULE, BEFORE THE DIRECTORY. This is the only caller of IDirectoryAdmin.CreateUserAsync, so nothing
        // creates an account without passing it. Deny by default. The verdict is logged, never the address.
        var verdict = EmailDomainPolicy.Check(request.Email, _options.Value.Admin.AllowedEmailDomains);
        if (verdict != EmailDomainVerdict.Allowed)
        {
            if (verdict == EmailDomainVerdict.NotConfigured)
            {
                LogCreationNotConfigured(_logger, CreateSurface);
            }
            else
            {
                LogCreationRefused(_logger, CreateSurface, verdict.ToString());
            }

            return AdminResult.Failed(AdminOutcome.EmailDomainNotAllowed);
        }

        var result = await _directory.CreateUserAsync(request, cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            return result;
        }

        var flagged = await _roleChange.HandleAsync(result.User.Subject, cancellationToken);
        LogGroupsChanged(_logger, result.Added?.Count ?? 0, result.Removed?.Count ?? 0, flagged);
        return result;
    }

    public Task<AdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken) =>
        _directory.GetGroupsAsync(subject, cancellationToken);

    /// <summary>Sets membership, then flags the user's sessions so the next request re-exchanges.</summary>
    public async Task<AdminResult> SetGroupsAsync(string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(groupPaths);

        var result = await _directory.SetGroupsAsync(subject, groupPaths, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        // Nothing actually moved: no reason to make every session of this user re-exchange.
        if (result.Added is { Count: 0 } && result.Removed is { Count: 0 })
        {
            return result;
        }

        var flagged = await _roleChange.HandleAsync(subject, cancellationToken);
        LogGroupsChanged(_logger, result.Added?.Count ?? 0, result.Removed?.Count ?? 0, flagged);
        return result;
    }

    /// <summary>
    /// Disables the user and ends their sessions outright.
    /// </summary>
    /// <remarks>
    /// A force-refresh would be wrong here. A refresh goes back to the identity provider, which does not know the user was disabled in
    /// Keycloak; the exchange would fail and the session would end anyway, but only on the user's next request and only after a round
    /// trip that can fail transiently. A disable is definitive, so the sessions go now, each reported to its portal as ended by the
    /// system. THE ORDER IS THE POINT: upstream first, sessions second. A session ended for a disable Keycloak never performed looks
    /// exactly like a successful disable for as long as the user stays signed out, and they can sign straight back in.
    /// </remarks>
    public async Task<AdminResult> DisableAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var result = await _directory.SetEnabledAsync(subject, enabled: false, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var ended = 0;
        foreach (var key in await _sessions.GetSessionKeysForSubjectAsync(subject, cancellationToken))
        {
            // An END rather than a delete, so the portal is told.
            if (await _sessions.EndAsync(key, subject, SessionEnd.BySystem(SessionEndDetails.UserDisabled, DateTimeOffset.UtcNow), cancellationToken))
            {
                ended++;
            }
        }

        LogDisabled(_logger, ended);
        return result;
    }

    /// <summary>Re-enables the user. Existing sessions are already gone; they sign in again.</summary>
    public Task<AdminResult> EnableAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return _directory.SetEnabledAsync(subject, enabled: true, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Group membership changed: +{Added} -{Removed}; {Flagged} session(s) flagged.")]
    private static partial void LogGroupsChanged(ILogger logger, int added, int removed, int flagged);

    [LoggerMessage(Level = LogLevel.Information, Message = "User disabled; {Ended} session(s) ended.")]
    private static partial void LogDisabled(ILogger logger, int ended);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User creation refused on {Surface} before the directory was called: {Verdict}.")]
    private static partial void LogCreationRefused(ILogger logger, string surface, string verdict);

    [LoggerMessage(Level = LogLevel.Error, Message = "User creation refused on {Surface}: Auth:Admin:AllowedEmailDomains is empty, so no e-mail domain is allowed (fail closed). Set it for this environment.")]
    private static partial void LogCreationNotConfigured(ILogger logger, string surface);
}
