using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.SessionEvents;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>The loop around the dispatcher: off until a portal has a receiver, and never a way to take the service down.</summary>
public sealed class SessionEndedDispatchServiceTests
{
    private readonly CountingOutbox _outbox = new();
    private readonly AuthOptions _options = TestConfig.Options();

    private SessionEndedDispatchService Sut(CapturingLogger<SessionEndedDispatchService>? log = null)
    {
        var time = TimeProvider.System;
        var dispatcher = new SessionEndedDispatcher(
            _outbox, new NeverSender(), TestConfig.Portals(_options), TestConfig.Wrap(_options), time, new CapturingLogger<SessionEndedDispatcher>());
        return new SessionEndedDispatchService(dispatcher, TestConfig.Wrap(_options), TestConfig.Portals(_options), time, log ?? new CapturingLogger<SessionEndedDispatchService>());
    }

    [Fact]
    public async Task With_no_receiver_configured_it_makes_one_settling_pass_and_stops_polling()
    {
        _options.SessionEndDelivery.PollInterval = TimeSpan.FromSeconds(1);
        var log = new CapturingLogger<SessionEndedDispatchService>();
        var sut = Sut(log);

        await sut.StartAsync(default);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(_options.SessionEndDelivery.PollInterval + TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, _outbox.Claims);
        Assert.Equal(1, _outbox.Purges);
        Assert.Contains(log.Entries, e => e.Message.Contains("No portal has a session-ended receiver configured", StringComparison.Ordinal));
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task With_a_receiver_it_keeps_polling_until_it_is_stopped_and_names_how_each_portal_is_called()
    {
        _options.Session.SweepInterval = TimeSpan.FromMinutes(30);
        _options.SessionEndDelivery.PollInterval = TimeSpan.FromSeconds(1);
        _options.Portal.SessionEvents.Url = "https://api.example.com/api/integration/auth-backend/session-ended";
        _options.Portal.SessionEvents.Key = "a-shared-key-value";
        var log = new CapturingLogger<SessionEndedDispatchService>();
        var sut = Sut(log);

        await sut.StartAsync(default);
        await Task.Delay(TimeSpan.FromMilliseconds(2400));
        await sut.StopAsync(default);

        Assert.True(_outbox.Claims >= 3, $"claimed {_outbox.Claims} times");
        var line = Assert.Single(log.Entries, e => e.Message.Contains("Session-ended events go to", StringComparison.Ordinal));
        Assert.Contains("shared key", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a-shared-key-value", string.Join("\n", log.Entries.Select(e => e.Message + string.Join(",", e.Fields.Values))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_outbox_is_logged_and_the_loop_survives()
    {
        _options.SessionEndDelivery.PollInterval = TimeSpan.FromSeconds(1);
        _options.Portal.SessionEvents.Url = "https://api.example.com/api/integration/auth-backend/session-ended";
        _outbox.Fail = true;
        var log = new CapturingLogger<SessionEndedDispatchService>();
        var sut = Sut(log);

        await sut.StartAsync(default);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await sut.StopAsync(default);

        Assert.True(_outbox.Claims >= 2, $"claimed {_outbox.Claims} times");
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Field("Surface") == SessionEndedDispatcher.Surface);
    }

    private sealed class CountingOutbox : ISessionEndedOutbox
    {
        public int Claims { get; private set; }

        public int Purges { get; private set; }

        public bool Fail { get; set; }

        public Task<IReadOnlyList<SessionEndedEvent>> ClaimDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken)
        {
            Claims++;
            return Fail ? throw new InvalidOperationException("sql down") : Task.FromResult<IReadOnlyList<SessionEndedEvent>>([]);
        }

        public Task MarkDeliveredAsync(Guid eventId, string outcome, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ScheduleRetryAsync(Guid eventId, DateTimeOffset nextAttemptAt, string outcome, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AbandonAsync(Guid eventId, string outcome, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> PurgeSettledAsync(DateTimeOffset before, CancellationToken cancellationToken)
        {
            Purges++;
            return Task.FromResult(0);
        }
    }

    private sealed class NeverSender : ISessionEndedSender
    {
        public Task<SessionEndedDelivery> SendAsync(SessionEndedEvent sessionEnded, SessionEventsOptions target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no event is queued in these tests");
    }
}
