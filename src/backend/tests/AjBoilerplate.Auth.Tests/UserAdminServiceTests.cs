using Microsoft.Extensions.Logging.Abstractions;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class UserAdminServiceTests
{
    private const string Subject = "user-1";

    private readonly FakeDirectoryAdmin _directory = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly UserAdminService _admin;

    public UserAdminServiceTests() =>
        _admin = new UserAdminService(
            _directory,
            _sessions,
            new ForceRefreshHandler(_sessions, NullLogger<ForceRefreshHandler>.Instance),
            TestConfig.Wrap(new AuthOptions()),
            NullLogger<UserAdminService>.Instance);

    private async Task<string> GivenASessionAsync(string key = "k1")
    {
        await _sessions.CreateAsync(
            key,
            new AuthSession("jwt", DateTimeOffset.UnixEpoch.AddYears(60), "rt", Subject, TestConfig.ClientId, false, DateTimeOffset.UnixEpoch),
            TimeSpan.FromHours(8),
            CancellationToken.None);
        return key;
    }

    [Fact]
    public async Task A_group_change_flags_the_users_live_sessions()
    {
        var key = await GivenASessionAsync();

        var result = await _admin.SetGroupsAsync(Subject, ["/my-app/editors"], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(_sessions.Sessions[key].ForceRefresh);
    }

    [Fact]
    public async Task A_change_that_changes_nothing_does_not_disturb_live_sessions()
    {
        var key = await GivenASessionAsync();
        _directory.Groups.Add("/my-app/editors");

        await _admin.SetGroupsAsync(Subject, ["/my-app/editors"], CancellationToken.None);

        Assert.False(_sessions.Sessions[key].ForceRefresh);
    }

    [Fact]
    public async Task Nothing_is_flagged_when_keycloak_refused()
    {
        var key = await GivenASessionAsync();
        _directory.Next = AdminResult.Denied;

        var result = await _admin.SetGroupsAsync(Subject, ["/my-app/editors"], CancellationToken.None);

        // Flagging a session whose Keycloak write failed would refresh it, return the SAME token
        // and look successful.
        Assert.Equal(AdminOutcome.Denied, result.Outcome);
        Assert.False(_sessions.Sessions[key].ForceRefresh);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_rather_than_refreshing_them()
    {
        await GivenASessionAsync("k1");
        await GivenASessionAsync("k2");

        var result = await _admin.DisableAsync(Subject, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(_directory.Enabled);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task A_failed_disable_leaves_the_sessions_alone()
    {
        await GivenASessionAsync();
        _directory.Next = AdminResult.Transient;

        var result = await _admin.DisableAsync(Subject, CancellationToken.None);

        Assert.Equal(AdminOutcome.Transient, result.Outcome);
        Assert.Single(_sessions.Sessions);
    }
}

internal sealed class FakeDirectoryAdmin : IDirectoryAdmin
{
    public HashSet<string> Groups { get; } = new(StringComparer.Ordinal);

    public bool Enabled { get; private set; } = true;

    /// <summary>When set, the next call fails with this instead of doing anything.</summary>
    public AdminResult? Next { get; set; }

    /// <summary>Every call that reached the directory, of any kind.</summary>
    public int Calls { get; private set; }

    public Task<(AdminOutcome Outcome, IReadOnlyList<RealmUser> Users)> SearchUsersAsync(string term, int limit, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<(AdminOutcome, IReadOnlyList<RealmUser>)>((Next?.Outcome ?? AdminOutcome.Success, [new RealmUser("u1", "user1", "user1@example.com", null, null, true)]));
    }

    public Task<MembersView> GetPortalMembersAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new MembersView(AdminOutcome.Success, [], [new PortalAccess("/my-app", AdminOutcome.Success)]));
    }

    public Task<GroupTreeView> GetGroupTreeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new GroupTreeView(AdminOutcome.Success, [], [new PortalAccess("/my-app", AdminOutcome.Success)]));
    }

    /// <summary>How many times account creation reached the directory.</summary>
    public int CreateCalls { get; private set; }

    public Task<AdminResult> CreateUserAsync(NewUser request, CancellationToken cancellationToken)
    {
        CreateCalls++;
        Calls++;
        if (Next is { } failure)
        {
            return Task.FromResult(failure);
        }

        ArgumentNullException.ThrowIfNull(request);
        Groups.Add(request.GroupPath);
        return Task.FromResult(new AdminResult(
            AdminOutcome.Success,
            Groups: [.. Groups],
            Added: [request.GroupPath],
            Removed: [],
            User: new RealmUser("user-1", request.Email, request.Email, request.FirstName, request.LastName, Enabled: true)));
    }

    public Task<AdminResult> GetGroupsAsync(string subject, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Next ?? new AdminResult(AdminOutcome.Success, Groups: [.. Groups], Portals: [new PortalAccess("/my-app", AdminOutcome.Success)]));
    }

    public Task<AdminResult> SetGroupsAsync(string subject, IReadOnlyCollection<string> groupPaths, CancellationToken cancellationToken)
    {
        Calls++;
        if (Next is { } failure)
        {
            return Task.FromResult(failure);
        }

        var desired = groupPaths.ToHashSet(StringComparer.Ordinal);
        var added = desired.Except(Groups, StringComparer.Ordinal).ToList();
        var removed = Groups.Except(desired, StringComparer.Ordinal).ToList();

        Groups.Clear();
        Groups.UnionWith(desired);

        return Task.FromResult(new AdminResult(AdminOutcome.Success, Groups: desired, Added: added, Removed: removed));
    }

    /// <summary>Writes the flag, because <see cref="UserAdminService.DisableAsync"/>'s contract (end the sessions only AFTER the upstream agreed) is what this fake exists to exercise.</summary>
    public Task<AdminResult> SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken)
    {
        Calls++;
        if (Next is { } failure)
        {
            return Task.FromResult(failure);
        }

        Enabled = enabled;
        return Task.FromResult(new AdminResult(AdminOutcome.Success));
    }
}
