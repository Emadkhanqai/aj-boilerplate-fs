using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Observability;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using AjBoilerplate.Auth.Redis;
using AjBoilerplate.Auth.Sql;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// The session-ended outbox against a real SQL Server and Redis, composed as production composes
/// them. What only a real database can show: that the delete and the event commit
/// together, that a second end of the same session queues nothing, that the sweeper's READ-BACK key
/// gives the same portal session id as the cookie-derived one, and that two claimants never get the
/// same event. Skipped, never passed, without <c>AUTH_TEST_SQL</c> and <c>AUTH_TEST_REDIS</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionEndedOutboxTests : IAsyncLifetime
{
    private const string ReceiverUrl = "https://api.example.com/api/integration/auth-backend/session-ended";

    private readonly string _subject = "sub-" + Guid.NewGuid().ToString("N");
    private readonly string _database = "auth_test_" + Guid.NewGuid().ToString("N");
    private ServiceProvider? _services;

    private ISessionStore Store => _services!.GetRequiredService<ISessionStore>();

    private SqlSessionStore Sql => _services!.GetRequiredService<SqlSessionStore>();

    private ISessionEndedOutbox Outbox => _services!.GetRequiredService<ISessionEndedOutbox>();

    private PortalSessionId Ids => _services!.GetRequiredService<PortalSessionId>();

    public async Task InitializeAsync()
    {
        var sql = Environment.GetEnvironmentVariable(DurableFactAttribute.SqlVariable);
        var redis = Environment.GetEnvironmentVariable(RedisFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(redis))
        {
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redis,
                ["ConnectionStrings:AuthDb"] = $"{sql.TrimEnd(';')};Database={_database}",
            })
            .Build();

        var options = TestConfig.TwoPortals();
        options.Session.EncryptionKey = SessionTokenProtector.GenerateKey();

        // Portal #0 receives events; the second portal deliberately does not.
        options.Portal.SessionEvents.Url = ReceiverUrl;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TestConfig.Wrap(options));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<PortalRegistry>();
        services.AddAuthRedis(configuration);
        services.AddSingleton<PortalSessionId>();
        services.AddAuthSql(configuration);
        _services = services.BuildServiceProvider();

        await using var db = await Contexts().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is null)
        {
            return;
        }

        await using (var db = await Contexts().CreateDbContextAsync())
        {
            await db.Database.EnsureDeletedAsync();
        }

        await _services.DisposeAsync();
    }

    private IDbContextFactory<AuthDbContext> Contexts() => _services!.GetRequiredService<IDbContextFactory<AuthDbContext>>();

    private async Task<List<SessionEndedEventRow>> RowsAsync()
    {
        await using var db = await Contexts().CreateDbContextAsync();
        return await db.SessionEndedOutbox.AsNoTracking().OrderBy(x => x.EnqueuedAt).ToListAsync();
    }

    private async Task<(string Cookie, string Key, DateTimeOffset SignedIn)> SignedInAsync(string clientId = TestConfig.ClientId)
    {
        var cookie = SessionIds.NewRandom();
        var key = SessionIds.StorageKey(cookie);
        var signedIn = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600);
        await Store.CreateAsync(
            key,
            new AuthSession(Jwt.Create(_subject, DateTimeOffset.UtcNow.AddMinutes(30), clientId, "requester"), DateTimeOffset.UtcNow.AddMinutes(30), "rt-1", _subject, clientId, false, signedIn),
            TimeSpan.FromHours(8),
            default);
        return (cookie, key, signedIn);
    }

    [DurableFact]
    public async Task Ending_a_session_queues_exactly_one_event_with_the_portal_facing_id_and_never_the_key()
    {
        var (_, key, signedIn) = await SignedInAsync();
        RequestCorrelation.Current = "req-abc-123";
        bool first;
        bool second;
        try
        {
            first = await Store.EndAsync(key, _subject, SessionEnd.LoggedOut(DateTimeOffset.UtcNow), default);
            second = await Store.EndAsync(key, _subject, SessionEnd.LoggedOut(DateTimeOffset.UtcNow), default);
        }
        finally
        {
            RequestCorrelation.Current = null;
        }

        Assert.True(first);
        Assert.False(second);
        Assert.Null(await Store.GetAsync(key, default));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(Ids.For(key), row.SessionId);
        Assert.NotEqual(key, row.SessionId);
        Assert.Equal(TestConfig.ClientId, row.ClientId);
        Assert.Equal("logged-out", row.Reason);
        Assert.Equal("user_logout", row.Detail);
        Assert.Equal(signedIn, row.SessionStartedAt);
        Assert.Equal("req-abc-123", row.CorrelationId);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.DeliveredAt);
    }

    [DurableFact]
    public async Task A_portal_without_a_receiver_gets_its_session_ended_and_nothing_queued()
    {
        var (_, key, _) = await SignedInAsync(TestConfig.OtherClientId);

        Assert.True(await Store.EndAsync(key, _subject, SessionEnd.LoggedOut(DateTimeOffset.UtcNow), default));

        Assert.Null(await Store.GetAsync(key, default));
        Assert.Empty(await RowsAsync());
    }

    /// <summary>
    /// The sweeper ends a session from a key READ BACK out of a <c>char(43)</c> column. The id it
    /// sends must be the one the portal was given from the cookie, or the portal cannot pair the end
    /// with its "Session created" row (a fixed-width key column must hand back exactly the key that was written).
    /// </summary>
    [DurableFact]
    public async Task The_sweeper_queues_session_expired_with_the_same_id_the_portal_saw()
    {
        var (_, key, _) = await SignedInAsync();
        var (_, keptKey, _) = await SignedInAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var db = await Contexts().CreateDbContextAsync())
        {
            await db.Sessions.Where(x => x.SessionKey == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, deadline));
        }

        var ended = await Sql.EndExpiredAsync(500, TimeSpan.FromHours(12), default);

        Assert.Equal(1, ended);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(Ids.For(key), row.SessionId);
        Assert.Equal("session-expired", row.Reason);
        Assert.Equal("idle_timeout", row.Detail);
        Assert.Equal(deadline, row.OccurredAt);
        Assert.Null(row.CorrelationId);
        Assert.NotNull(await Store.GetAsync(keptKey, default));
    }

    /// <summary>
    /// A session still inside its idle window but past the absolute lifetime is ended by the sweep too, reported as the absolute
    /// lifetime and dated at that deadline; a live session of the same age bound is not touched.
    /// </summary>
    [DurableFact]
    public async Task The_sweeper_ends_a_session_past_its_absolute_lifetime_even_while_it_is_idle_fresh()
    {
        var (_, key, _) = await SignedInAsync();
        var (_, keptKey, _) = await SignedInAsync();
        var created = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-13).ToUnixTimeSeconds());
        await using (var db = await Contexts().CreateDbContextAsync())
        {
            await db.Sessions.Where(x => x.SessionKey == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, created));
        }

        var ended = await Sql.EndExpiredAsync(500, TimeSpan.FromHours(12), default);

        Assert.Equal(1, ended);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal("session-expired", row.Reason);
        Assert.Equal("absolute_lifetime", row.Detail);
        Assert.Equal(created + TimeSpan.FromHours(12), row.OccurredAt);
        Assert.NotNull(await Store.GetAsync(keptKey, default));
    }

    [DurableFact]
    public async Task A_handed_off_session_keeps_its_lineage_through_sql_and_the_cache()
    {
        var rootKey = SessionIds.StorageKey(SessionIds.NewRandom());
        var key = SessionIds.StorageKey(SessionIds.NewRandom());
        await Store.CreateAsync(
            key,
            new AuthSession(Jwt.Create(_subject, DateTimeOffset.UtcNow.AddMinutes(30), TestConfig.OtherClientId, "requester"), DateTimeOffset.UtcNow.AddMinutes(30), null, _subject, TestConfig.OtherClientId, false, DateTimeOffset.UtcNow, rootKey),
            TimeSpan.FromHours(8),
            default);

        var fromSql = await _services!.GetRequiredService<SqlSessionStore>().GetAsync(key, default);

        Assert.Equal(rootKey, fromSql!.RefreshVia);
        Assert.Null(fromSql.EntraRefreshToken);
        Assert.Equal(rootKey, (await Store.GetAsync(key, default))!.RefreshVia);
    }

    [DurableFact]
    public async Task Two_claimants_never_get_the_same_event_and_a_settled_one_is_not_claimed_again()
    {
        var (_, key, _) = await SignedInAsync();
        await Store.EndAsync(key, _subject, SessionEnd.LoggedOut(DateTimeOffset.UtcNow), default);

        var claims = await Task.WhenAll(
            Outbox.ClaimDueAsync(10, TimeSpan.FromMinutes(2), default),
            Outbox.ClaimDueAsync(10, TimeSpan.FromMinutes(2), default));

        var claimed = Assert.Single(claims.SelectMany(c => c));
        Assert.Equal(1, claimed.Attempt);
        Assert.Empty(await Outbox.ClaimDueAsync(10, TimeSpan.FromMinutes(2), default));

        // A retry scheduled in the past is due again, as attempt 2.
        await Outbox.ScheduleRetryAsync(claimed.EventId, DateTimeOffset.UtcNow.AddSeconds(-1), "http_503", default);
        var again = Assert.Single(await Outbox.ClaimDueAsync(10, TimeSpan.FromMinutes(2), default));
        Assert.Equal(2, again.Attempt);

        // The idempotency key survives every retry: the receiver deduplicates on it.
        Assert.Equal(claimed.EventId, again.EventId);
        Assert.Equal(claimed.SessionId, again.SessionId);

        await Outbox.MarkDeliveredAsync(claimed.EventId, "recorded", default);
        await Outbox.ScheduleRetryAsync(claimed.EventId, DateTimeOffset.UtcNow.AddSeconds(-1), "late", default);
        Assert.Empty(await Outbox.ClaimDueAsync(10, TimeSpan.FromMinutes(2), default));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal("recorded", row.LastOutcome);
        Assert.NotNull(row.DeliveredAt);

        Assert.Equal(0, await Outbox.PurgeSettledAsync(DateTimeOffset.UtcNow.AddMinutes(-1), default));
        Assert.Equal(1, await Outbox.PurgeSettledAsync(DateTimeOffset.UtcNow.AddMinutes(1), default));
        Assert.Empty(await RowsAsync());
    }
}
