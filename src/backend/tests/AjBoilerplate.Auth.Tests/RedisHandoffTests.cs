using StackExchange.Redis;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using AjBoilerplate.Auth.Redis;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// The portal hand-off's Redis half, against a real Redis (<see cref="RedisFactAttribute"/>):
/// the ticket store's single use and TTL, and the <c>via</c> field of a handed-off session.
/// SKIPPED, never passed, without <c>AUTH_TEST_REDIS</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisHandoffTests : IAsyncLifetime
{
    private readonly string _ticketKey = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _sessionKey = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _subject = "sub-" + Guid.NewGuid().ToString("N");
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-19T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private ConnectionMultiplexer? _redis;

    private IDatabase Db => _redis!.GetDatabase();

    private static HandoffTicket Ticket() => new("source-key", "kc-user", "my-app", "https://app.example.com", "other-app", "/items/42");

    public async Task InitializeAsync()
    {
        var target = Environment.GetEnvironmentVariable(RedisFactAttribute.Variable);
        if (!string.IsNullOrWhiteSpace(target))
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(target);
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await Db.KeyDeleteAsync(RedisKeys.Handoff(_ticketKey));
            await Db.KeyDeleteAsync(RedisKeys.Session(_sessionKey));
            await Db.KeyDeleteAsync(RedisKeys.SubjectIndex(_subject));
            await _redis.DisposeAsync();
        }
    }

    [RedisFact]
    public async Task A_ticket_is_taken_exactly_once()
    {
        var store = new RedisHandoffTicketStore(_redis!);
        await store.SaveAsync(_ticketKey, Ticket(), TimeSpan.FromSeconds(60), CancellationToken.None);

        var first = await store.TakeAsync(_ticketKey, CancellationToken.None);
        var second = await store.TakeAsync(_ticketKey, CancellationToken.None);

        Assert.Equal(Ticket(), first);
        Assert.Null(second);
        Assert.False(await Db.KeyExistsAsync(RedisKeys.Handoff(_ticketKey)));
    }

    [RedisFact]
    public async Task Concurrent_takes_of_one_ticket_yield_one_winner()
    {
        var store = new RedisHandoffTicketStore(_redis!);
        await store.SaveAsync(_ticketKey, Ticket(), TimeSpan.FromSeconds(60), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.TakeAsync(_ticketKey, CancellationToken.None)));

        Assert.Single(results, r => r is not null);
    }

    [RedisFact]
    public async Task A_ticket_carries_its_lifetime_as_a_ttl_under_the_service_prefix()
    {
        var store = new RedisHandoffTicketStore(_redis!);
        await store.SaveAsync(_ticketKey, Ticket(), TimeSpan.FromSeconds(60), CancellationToken.None);

        var ttl = await Db.KeyTimeToLiveAsync(RedisKeys.Handoff(_ticketKey));

        Assert.StartsWith("auth:handoff:", RedisKeys.Handoff(_ticketKey), StringComparison.Ordinal);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
    }

    [RedisFact]
    public async Task A_handed_off_session_reads_back_with_its_lineage_and_no_refresh_token()
    {
        var store = new RedisSessionStore(_redis!, new SessionTokenProtector(SessionTokenProtector.GenerateKey()), _time);
        await store.CreateAsync(
            _sessionKey,
            new AuthSession("jwt", _time.Now.AddMinutes(5), null, _subject, "other-app", false, _time.Now, RefreshVia: "root-key"),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        var read = await store.GetAsync(_sessionKey, CancellationToken.None);

        Assert.Equal("root-key", read!.RefreshVia);
        Assert.Null(read.EntraRefreshToken);
    }

    [RedisFact]
    public async Task An_ordinary_session_reads_back_with_no_lineage()
    {
        var store = new RedisSessionStore(_redis!, new SessionTokenProtector(SessionTokenProtector.GenerateKey()), _time);
        await store.CreateAsync(
            _sessionKey,
            new AuthSession("jwt", _time.Now.AddMinutes(5), "rt", _subject, "other-app", false, _time.Now),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.Null((await store.GetAsync(_sessionKey, CancellationToken.None))!.RefreshVia);
    }
}
