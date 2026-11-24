using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.SessionEvents;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using AjBoilerplate.Auth.Sql;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// Which code path ends a session, and with which of the three reasons (docs/auth/session-ended-contract.md). Every END goes through
/// <see cref="ISessionStore.EndAsync"/>, which is what queues the event; an infrastructure fault
/// keeps the session and so ends nothing.
/// </summary>
public sealed class SessionEndReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _store = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly AuthOptions _options = TestConfig.Options();
    private readonly string _sid = SessionIds.NewRandom();

    private string Key => SessionIds.StorageKey(_sid);

    private SessionValidator Validator() =>
        new(_store, new FakeLock(), _entra, _keycloak, TestConfig.Wrap(_options), _time, Log.For<SessionValidator>(), new InMemoryRevokedSessionMarks());

    // jwtLifetime below the 2-minute refresh skew makes the validate refresh.
    private void Seed(TimeSpan jwtLifetime, DateTimeOffset? signedIn = null, string? refreshToken = "rt-1", string? refreshVia = null)
    {
        _store.Sessions[Key] = new AuthSession(
            Jwt.Create("kc-user", Now + jwtLifetime, TestConfig.ClientId, "requester"),
            Now + jwtLifetime,
            refreshToken,
            "kc-user",
            TestConfig.ClientId,
            ForceRefresh: false,
            signedIn ?? Now - TimeSpan.FromHours(1),
            refreshVia);
    }

    private SessionEnd OnlyEnd()
    {
        var ended = Assert.Single(_store.Ended);
        Assert.Equal(Key, ended.SessionKey);
        Assert.False(_store.Sessions.ContainsKey(Key));
        return ended.Ending;
    }

    [Fact]
    public async Task Logout_is_logged_out()
    {
        Seed(TimeSpan.FromMinutes(30));
        var signIn = new SignInService(
            _store, new InMemoryLoginStore(), _entra, _keycloak, TestConfig.Wrap(_options), _time, Log.For<SignInService>());

        await signIn.SignOutAsync(_sid, default);

        Assert.Equal(new SessionEnd(SessionEndReason.LoggedOut, SessionEndDetails.UserLogout, Now), OnlyEnd());
    }

    [Fact]
    public async Task Logout_of_a_session_that_is_already_gone_reports_nothing()
    {
        var signIn = new SignInService(
            _store, new InMemoryLoginStore(), _entra, _keycloak, TestConfig.Wrap(_options), _time, Log.For<SignInService>());

        await signIn.SignOutAsync(_sid, default);

        Assert.Empty(_store.Ended);
    }

    [Fact]
    public async Task The_absolute_lifetime_is_session_expired_dated_at_the_deadline_not_at_detection()
    {
        var signedIn = Now - TimeSpan.FromHours(13);
        Seed(TimeSpan.FromMinutes(30), signedIn);

        var result = await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(ValidationStatus.Unauthenticated, result.Status);
        Assert.Equal(
            new SessionEnd(SessionEndReason.SessionExpired, SessionEndDetails.AbsoluteLifetime, signedIn + _options.Session.AbsoluteLifetime),
            OnlyEnd());
    }

    [Fact]
    public async Task Entra_invalid_grant_is_signed_out_by_system()
    {
        Seed(TimeSpan.FromSeconds(30));
        _entra.NextRefresh = EntraTokenResult.InvalidGrant;

        await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(new SessionEnd(SessionEndReason.SignedOutBySystem, SessionEndDetails.EntraInvalidGrant, Now), OnlyEnd());
    }

    [Fact]
    public async Task Keycloak_denial_is_signed_out_by_system()
    {
        Seed(TimeSpan.FromSeconds(30));
        _keycloak.Next = ExchangeResult.Denied;

        await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(new SessionEnd(SessionEndReason.SignedOutBySystem, SessionEndDetails.KeycloakDenied, Now), OnlyEnd());
    }

    [Fact]
    public async Task No_client_role_is_signed_out_by_system()
    {
        Seed(TimeSpan.FromSeconds(30));
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("kc-user", Now + TimeSpan.FromMinutes(5), TestConfig.ClientId));

        await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(new SessionEnd(SessionEndReason.SignedOutBySystem, SessionEndDetails.NoClientRole, Now), OnlyEnd());
    }

    [Fact]
    public async Task No_refresh_token_is_signed_out_by_system()
    {
        Seed(TimeSpan.FromSeconds(30), refreshToken: null);

        await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(new SessionEnd(SessionEndReason.SignedOutBySystem, SessionEndDetails.NoRefreshToken, Now), OnlyEnd());
    }

    [Fact]
    public async Task A_borrowed_grant_whose_owner_is_gone_is_signed_out_by_system()
    {
        Seed(TimeSpan.FromSeconds(30), refreshToken: null, refreshVia: SessionIds.StorageKey(SessionIds.NewRandom()));

        await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(new SessionEnd(SessionEndReason.SignedOutBySystem, SessionEndDetails.RefreshLineageEnded, Now), OnlyEnd());
    }

    /// <summary>The estate invariant: an infrastructure fault never ends a session, so it reports nothing.</summary>
    [Theory]
    [InlineData("entra")]
    [InlineData("keycloak")]
    public async Task A_transient_fault_ends_nothing_and_reports_nothing(string provider)
    {
        Seed(TimeSpan.FromSeconds(30));
        if (provider == "entra")
        {
            _entra.NextRefresh = EntraTokenResult.Transient;
        }
        else
        {
            _keycloak.Next = ExchangeResult.Transient;
        }

        var result = await Validator().ValidateAsync(_sid, TestConfig.Host, default);

        Assert.Equal(ValidationStatus.Ok, result.Status);
        Assert.Empty(_store.Ended);
        Assert.True(_store.Sessions.ContainsKey(Key));
    }

    [Fact]
    public void A_sweep_reports_the_idle_deadline_when_it_came_first()
    {
        var row = new SqlSessionStore.EndingSession("s", "c", CreatedAt: Now - TimeSpan.FromHours(9), ExpiresAt: Now - TimeSpan.FromMinutes(10));

        var end = SqlSessionStore.ExpiredEnd(row, TimeSpan.FromHours(12));

        Assert.Equal(new SessionEnd(SessionEndReason.SessionExpired, SessionEndDetails.IdleTimeout, row.ExpiresAt), end);
    }

    [Fact]
    public void A_sweep_reports_the_absolute_deadline_when_it_came_first()
    {
        var created = Now - TimeSpan.FromHours(14);
        var row = new SqlSessionStore.EndingSession("s", "c", created, ExpiresAt: Now - TimeSpan.FromMinutes(10));

        var end = SqlSessionStore.ExpiredEnd(row, TimeSpan.FromHours(12));

        Assert.Equal(new SessionEnd(SessionEndReason.SessionExpired, SessionEndDetails.AbsoluteLifetime, created + TimeSpan.FromHours(12)), end);
    }

    [Fact]
    public void A_sweep_of_a_session_with_no_recorded_sign_in_reports_the_idle_deadline()
    {
        var row = new SqlSessionStore.EndingSession("s", "c", DateTimeOffset.UnixEpoch, ExpiresAt: Now - TimeSpan.FromMinutes(10));

        var end = SqlSessionStore.ExpiredEnd(row, TimeSpan.FromHours(12));

        Assert.Equal(SessionEndDetails.IdleTimeout, end.Detail);
    }

    [Theory]
    [InlineData(SessionEndReason.LoggedOut, "logged-out")]
    [InlineData(SessionEndReason.SessionExpired, "session-expired")]
    [InlineData(SessionEndReason.SignedOutBySystem, "signed-out-by-system")]
    public void Wire_values_are_the_contract_values_and_round_trip(SessionEndReason reason, string wire)
    {
        Assert.Equal(wire, SessionEndReasons.ToWire(reason));
        Assert.Equal(reason, SessionEndReasons.FromWire(wire));
    }
}

/// <summary>The dispatcher's settle decisions, over a fake outbox and a scripted sender.</summary>
public sealed class SessionEndedDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly FakeOutbox _outbox = new();
    private readonly ScriptedSender _sender = new();
    private readonly CapturingLogger<SessionEndedDispatcher> _log = new();
    private readonly AuthOptions _options = TestConfig.Options();

    public SessionEndedDispatcherTests()
    {
        _options.Portal.SessionEvents.Url = "https://api.example.com/api/integration/auth-backend/session-ended";
    }

    private SessionEndedDispatcher Sut() =>
        new(_outbox, _sender, TestConfig.Portals(_options), TestConfig.Wrap(_options), _time, _log);

    private static SessionEndedEvent Event(int attempt = 1, DateTimeOffset? enqueuedAt = null, string clientId = TestConfig.ClientId) => new(
        Guid.CreateVersion7(),
        new string('a', 32),
        clientId,
        SessionEndReasons.LoggedOut,
        SessionEndDetails.UserLogout,
        Now,
        Now - TimeSpan.FromHours(1),
        "req-1",
        enqueuedAt ?? Now,
        attempt);

    [Fact]
    public async Task A_2xx_marks_the_event_delivered()
    {
        var e = Event();
        _outbox.Due.Enqueue(e);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Delivered, 200, "recorded");

        Assert.Equal(1, await Sut().DispatchDueAsync(default));

        Assert.Equal(("delivered", "recorded"), _outbox.Settled[e.EventId]);
        Assert.Equal(_options.Portal.SessionEvents.Url, _sender.LastTarget!.Url);
    }

    [Fact]
    public async Task A_permanent_refusal_abandons_at_once_and_logs_an_error_with_the_surface_as_a_field()
    {
        var e = Event();
        _outbox.Due.Enqueue(e);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Permanent, 400, "http_400");

        await Sut().DispatchDueAsync(default);

        Assert.Equal(("abandoned", "http_400"), _outbox.Settled[e.EventId]);
        var line = Assert.Single(_log.Entries, x => x.Level == LogLevel.Error);
        Assert.Equal(SessionEndedDispatcher.Surface, line.Field("Surface"));
        Assert.Equal("refused_by_receiver", line.Field("AbandonReason"));
        Assert.Equal(400, line.Fields["Status"]);
    }

    [Fact]
    public async Task A_retryable_failure_is_rescheduled_with_backoff_and_logged_at_warning()
    {
        var e = Event(attempt: 3);
        _outbox.Due.Enqueue(e);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, 503, "http_503");

        await Sut().DispatchDueAsync(default);

        var expected = Now + SessionEndedDispatcher.Backoff(3, _options.SessionEndDelivery.InitialRetryDelay, _options.SessionEndDelivery.MaxRetryDelay);
        Assert.Equal(expected, _outbox.RetryAt[e.EventId]);
        Assert.Equal(TimeSpan.FromSeconds(60), expected - Now);
        var line = Assert.Single(_log.Entries, x => x.Level == LogLevel.Warning);
        Assert.Equal("http_503", line.Field("Outcome"));
        Assert.Equal(TestConfig.ClientId, line.Field("Portal"));
    }

    [Fact]
    public async Task A_retryable_failure_past_max_age_is_abandoned()
    {
        var e = Event(attempt: 40, enqueuedAt: Now - _options.SessionEndDelivery.MaxAge);
        _outbox.Due.Enqueue(e);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "timeout");

        await Sut().DispatchDueAsync(default);

        Assert.Equal(("abandoned", "timeout"), _outbox.Settled[e.EventId]);
        Assert.Equal("max_age", Assert.Single(_log.Entries, x => x.Level == LogLevel.Error).Field("AbandonReason"));
    }

    [Fact]
    public async Task An_event_for_a_portal_with_no_receiver_is_abandoned_without_sending()
    {
        var e = Event(clientId: "a-portal-nobody-configured");
        _outbox.Due.Enqueue(e);

        await Sut().DispatchDueAsync(default);

        Assert.Equal(("abandoned", "no_receiver"), _outbox.Settled[e.EventId]);
        Assert.Equal(0, _sender.Calls);
    }

    [Fact]
    public async Task A_retried_event_is_sent_again_under_the_same_event_id_and_session_id()
    {
        var first = Event(attempt: 1);
        _outbox.Due.Enqueue(first);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, 503, "http_503");
        await Sut().DispatchDueAsync(default);

        // The outbox hands the SAME row back as attempt 2 once its retry time passes.
        _outbox.Due.Enqueue(first with { Attempt = 2 });
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Delivered, 200, "recorded");
        await Sut().DispatchDueAsync(default);

        Assert.Equal([first.EventId, first.EventId], _sender.EventIds);
        Assert.Equal([first.SessionId, first.SessionId], _sender.SessionIds);
        Assert.Equal(("delivered", "recorded"), _outbox.Settled[first.EventId]);
    }

    [Fact]
    public async Task A_sender_that_throws_keeps_the_event_for_retry()
    {
        var e = Event();
        _outbox.Due.Enqueue(e);
        _sender.Throw = true;

        await Sut().DispatchDueAsync(default);

        Assert.True(_outbox.RetryAt.ContainsKey(e.EventId));
        Assert.False(_outbox.Settled.ContainsKey(e.EventId));
    }

    [Fact]
    public async Task No_line_carries_the_session_id()
    {
        var e = Event();
        _outbox.Due.Enqueue(e);
        _sender.Next = new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, 503, "http_503");

        await Sut().DispatchDueAsync(default);

        Assert.NotEmpty(_log.Entries);
        foreach (var entry in _log.Entries)
        {
            Assert.DoesNotContain(e.SessionId, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.Fields.Values, v => v is string s && s == e.SessionId);
        }
    }

    /// <summary>
    /// The event carries no user identifier and no credential: the portal pairs it by session id with its own "session created" row. Checked on the type, so a field added later
    /// fails here rather than in a portal database.
    /// </summary>
    [Fact]
    public void The_event_and_the_wire_body_have_no_user_or_credential_field()
    {
        string[] forbidden = ["subject", "sub", "email", "user", "name", "token", "cookie", "jwt", "authorization", "key"];
        var fields = typeof(SessionEndedEvent).GetProperties().Select(p => p.Name)
            .Concat(typeof(HttpSessionEndedSender.Payload).GetProperties().Select(p => p.Name))
            .Select(n => n.ToLowerInvariant())
            .ToList();

        Assert.Contains("sessionid", fields);
        foreach (var name in fields)
        {
            Assert.DoesNotContain(forbidden, f => name == f || name.Contains(f, StringComparison.Ordinal) && f.Length > 3);
        }
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(6, 480)]
    [InlineData(7, 900)]
    [InlineData(500, 900)]
    public void Backoff_doubles_from_the_initial_delay_and_stops_at_the_cap(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SessionEndedDispatcher.Backoff(attempt, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15)));

    private sealed class FakeOutbox : ISessionEndedOutbox
    {
        public ConcurrentQueue<SessionEndedEvent> Due { get; } = new();

        public Dictionary<Guid, (string State, string Outcome)> Settled { get; } = [];

        public Dictionary<Guid, DateTimeOffset> RetryAt { get; } = [];

        public Task<IReadOnlyList<SessionEndedEvent>> ClaimDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken)
        {
            var batch = new List<SessionEndedEvent>();
            while (batch.Count < max && Due.TryDequeue(out var e))
            {
                batch.Add(e);
            }

            return Task.FromResult<IReadOnlyList<SessionEndedEvent>>(batch);
        }

        public Task MarkDeliveredAsync(Guid eventId, string outcome, CancellationToken cancellationToken)
        {
            Settled[eventId] = ("delivered", outcome);
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(Guid eventId, DateTimeOffset nextAttemptAt, string outcome, CancellationToken cancellationToken)
        {
            RetryAt[eventId] = nextAttemptAt;
            return Task.CompletedTask;
        }

        public Task AbandonAsync(Guid eventId, string outcome, CancellationToken cancellationToken)
        {
            Settled[eventId] = ("abandoned", outcome);
            return Task.CompletedTask;
        }

        public Task<int> PurgeSettledAsync(DateTimeOffset before, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class ScriptedSender : ISessionEndedSender
    {
        public SessionEndedDelivery Next { get; set; } = new(SessionEndedDeliveryKind.Delivered, 200, "recorded");

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public SessionEventsOptions? LastTarget { get; private set; }

        public List<Guid> EventIds { get; } = [];

        public List<string> SessionIds { get; } = [];

        public Task<SessionEndedDelivery> SendAsync(SessionEndedEvent sessionEnded, SessionEventsOptions target, CancellationToken cancellationToken)
        {
            Calls++;
            EventIds.Add(sessionEnded.EventId);
            SessionIds.Add(sessionEnded.SessionId);
            LastTarget = target;
            return Throw ? throw new InvalidOperationException("sender bug") : Task.FromResult(Next);
        }
    }
}

/// <summary>
/// The configuration rules for a portal's receiver: an origin audience, https outside dev, and url-or-nothing.
/// </summary>
public sealed class SessionEventsOptionsTests
{
    private static ValidateOptionsResultLike Validate(Action<SessionEventsOptions> configure, bool development = false)
    {
        var options = TestConfig.Options();
        options.Session.EncryptionKey = SessionTokenProtector.GenerateKey();
        configure(options.Portal.SessionEvents);
        var result = new AuthOptionsValidator(development).Validate(null, options);
        return new ValidateOptionsResultLike(result.Succeeded, result.Failures?.ToList() ?? []);
    }

    private sealed record ValidateOptionsResultLike(bool Succeeded, List<string> Failures);

    [Fact]
    public void Unset_is_valid_and_means_no_delivery()
    {
        var options = new SessionEventsOptions();
        Assert.False(options.IsConfigured);
        Assert.DoesNotContain(Validate(_ => { }).Failures, f => f.Contains("SessionEvents", StringComparison.Ordinal));
    }

    [Fact]
    public void The_audience_defaults_to_the_origin_of_the_url()
    {
        var options = new SessionEventsOptions { Url = "https://api.example.com/api/integration/auth-backend/session-ended" };
        Assert.Equal("https://api.example.com", options.ResolvedAudience);
    }

    [Fact]
    public void An_https_url_with_its_origin_audience_is_accepted()
    {
        var result = Validate(e => e.Url = "https://api.example.com/api/integration/auth-backend/session-ended");
        Assert.DoesNotContain(result.Failures, f => f.Contains("SessionEvents", StringComparison.Ordinal));
    }

    [Fact]
    public void An_audience_with_a_path_is_refused()
    {
        var result = Validate(e =>
        {
            e.Url = "https://api.example.com/api/integration/auth-backend/session-ended";
            e.Audience = "https://api.example.com/api";
        });
        Assert.Contains(result.Failures, f => f.Contains("Auth:Portal:SessionEvents:Audience must be an origin", StringComparison.Ordinal));
    }

    [Fact]
    public void An_audience_without_a_url_is_refused()
    {
        var result = Validate(e => e.Audience = "https://api.example.com");
        Assert.Contains(result.Failures, f => f.Contains("Auth:Portal:SessionEvents:Audience is set but", StringComparison.Ordinal));
    }

    [Fact]
    public void Plain_http_is_refused_outside_development_because_the_request_carries_an_id_token()
    {
        var result = Validate(e => e.Url = "http://backend.internal/api/integration/auth-backend/session-ended");
        Assert.Contains(result.Failures, f => f.StartsWith("Auth:Portal:SessionEvents:Url is http.", StringComparison.Ordinal));
    }

    [Fact]
    public void Plain_http_is_tolerated_in_development()
    {
        var result = Validate(e => e.Url = "http://localhost:5100/api/integration/auth-backend/session-ended", development: true);
        Assert.DoesNotContain(result.Failures, f => f.Contains("SessionEvents", StringComparison.Ordinal));
    }
}

/// <summary>
/// <see cref="HttpSessionEndedSender"/> against a real HTTP receiver on loopback: the exact wire
/// body and headers of docs/auth/session-ended-contract.md, and the classification of every answer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HttpSessionEndedSenderTests : IAsyncLifetime
{
    private const string Token = "google-id-token-for-test";

    // Copied out inside the handler: Kestrel resets a request's header collection once it completes.
    private readonly ConcurrentQueue<(Dictionary<string, string> Headers, string Body)> _received = new();
    private WebApplication? _app;
    private string _base = string.Empty;

    /// <summary>What the receiver answers next: status and optional JSON body; or a delay.</summary>
    private (int Status, string? Body, TimeSpan Delay) _answer = (200, """{"outcome":"recorded"}""", TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/api/integration/auth-backend/session-ended", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            _received.Enqueue((http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), await reader.ReadToEndAsync()));
            if (_answer.Delay > TimeSpan.Zero)
            {
                await Task.Delay(_answer.Delay);
            }

            http.Response.StatusCode = _answer.Status;
            if (_answer.Body is not null)
            {
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(_answer.Body);
            }
        });
        await _app.StartAsync();
        _base = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static HttpSessionEndedSender Sender(string? token = Token, TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpSessionEndedSender.HttpClientName, http => http.Timeout = timeout ?? TimeSpan.FromSeconds(5));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new HttpSessionEndedSender(factory, new StaticTokens(token));
    }

    private SessionEventsOptions Target => new() { Url = _base + "/api/integration/auth-backend/session-ended" };

    private static SessionEndedEvent Event() => new(
        Guid.Parse("0192f0a0-0000-7000-8000-000000000001"),
        "0123456789abcdef0123456789abcdef",
        TestConfig.ClientId,
        SessionEndReasons.SessionExpired,
        SessionEndDetails.IdleTimeout,
        new DateTimeOffset(2026, 10, 22, 9, 15, 42, 345, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 22, 1, 0, 7, TimeSpan.Zero),
        null,
        new DateTimeOffset(2026, 10, 22, 9, 20, 0, TimeSpan.Zero),
        1);

    [Fact]
    public async Task The_body_and_headers_are_the_contract_exactly()
    {
        var result = await Sender().SendAsync(Event(), Target, default);

        Assert.Equal(new SessionEndedDelivery(SessionEndedDeliveryKind.Delivered, 200, "recorded"), result);
        var (headers, body) = Assert.Single(_received);
        Assert.Equal("Bearer " + Token, headers["Authorization"]);
        Assert.Equal("Bearer " + Token, headers["X-Serverless-Authorization"]);
        Assert.Equal("0192f0a0000070008000000000000001", headers["X-Request-Id"]);
        Assert.StartsWith("application/json", headers["Content-Type"], StringComparison.Ordinal);

        using var json = JsonDocument.Parse(body);
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            ["schemaVersion", "eventType", "eventId", "sessionId", "reason", "reasonDetail", "occurredAtUtc", "sessionStartedAtUtc", "identityProvider", "portalClientId", "correlationId"],
            names);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("session.ended", root.GetProperty("eventType").GetString());
        Assert.Equal("0192f0a0-0000-7000-8000-000000000001", root.GetProperty("eventId").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("sessionId").GetString());
        Assert.Equal("session-expired", root.GetProperty("reason").GetString());
        Assert.Equal("idle_timeout", root.GetProperty("reasonDetail").GetString());
        Assert.Equal("2026-10-22T09:15:42Z", root.GetProperty("occurredAtUtc").GetString());
        Assert.Equal("2026-10-22T01:00:07Z", root.GetProperty("sessionStartedAtUtc").GetString());
        Assert.Equal("Auth Backend (Keycloak)", root.GetProperty("identityProvider").GetString());
        Assert.Equal(TestConfig.ClientId, root.GetProperty("portalClientId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("correlationId").ValueKind);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, """{"outcome":"duplicate"}""", SessionEndedDeliveryKind.Delivered, "duplicate")]
    [InlineData(200, """{"outcome":"ignored-unknown-session"}""", SessionEndedDeliveryKind.Delivered, "ignored-unknown-session")]
    [InlineData(200, """{"outcome":"something the portal invented"}""", SessionEndedDeliveryKind.Delivered, "acknowledged")]
    [InlineData(204, null, SessionEndedDeliveryKind.Delivered, "acknowledged")]
    [InlineData(400, null, SessionEndedDeliveryKind.Permanent, "http_400")]
    [InlineData(422, null, SessionEndedDeliveryKind.Permanent, "http_422")]
    [InlineData(401, null, SessionEndedDeliveryKind.Retry, "http_401")]
    [InlineData(403, null, SessionEndedDeliveryKind.Retry, "http_403")]
    [InlineData(404, null, SessionEndedDeliveryKind.Retry, "http_404")]
    [InlineData(429, null, SessionEndedDeliveryKind.Retry, "http_429")]
    [InlineData(503, null, SessionEndedDeliveryKind.Retry, "http_503")]
    public async Task Every_answer_is_classified(int status, string? body, SessionEndedDeliveryKind kind, string outcome)
    {
        _answer = (status, body, TimeSpan.Zero);

        var result = await Sender().SendAsync(Event(), Target, default);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(status, result.Status);
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public async Task No_id_token_sends_nothing_and_is_retried()
    {
        var result = await Sender(token: null).SendAsync(Event(), Target, default);

        Assert.Equal(new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "id_token_unavailable"), result);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task A_timeout_is_retried()
    {
        _answer = (200, null, TimeSpan.FromSeconds(2));

        var result = await Sender(timeout: TimeSpan.FromMilliseconds(200)).SendAsync(Event(), Target, default);

        Assert.Equal(new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "timeout"), result);
    }

    [Fact]
    public async Task A_refused_connection_is_retried()
    {
        var closed = new SessionEventsOptions { Url = "http://127.0.0.1:1/api/integration/auth-backend/session-ended" };

        var result = await Sender().SendAsync(Event(), closed, default);

        Assert.Equal(new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "transport"), result);
    }

    [Fact]
    public async Task With_a_shared_key_the_call_carries_only_that_key_and_no_google_token_is_asked_for()
    {
        var tokens = new CountingTokens();
        var sender = SenderWith(tokens);
        var target = new SessionEventsOptions { Url = _base + "/api/integration/auth-backend/session-ended", Key = "a-shared-key-value" };

        var result = await sender.SendAsync(Event(), target, default);

        Assert.Equal(new SessionEndedDelivery(SessionEndedDeliveryKind.Delivered, 200, "recorded"), result);
        var (headers, body) = Assert.Single(_received);
        Assert.Equal("a-shared-key-value", headers[HttpSessionEndedSender.KeyHeader]);
        Assert.Equal("0192f0a0000070008000000000000001", headers["X-Request-Id"]);
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.False(headers.ContainsKey("X-Serverless-Authorization"));
        Assert.Equal(0, tokens.Asked); // off Google Cloud there is no metadata server to wait on
        Assert.DoesNotContain("a-shared-key-value", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_key_the_google_token_is_used_and_no_key_header_is_sent()
    {
        var result = await Sender().SendAsync(Event(), Target, default);

        Assert.Equal(SessionEndedDeliveryKind.Delivered, result.Kind);
        Assert.False(Assert.Single(_received).Headers.ContainsKey(HttpSessionEndedSender.KeyHeader));
    }

    [Theory]
    [InlineData(401, SessionEndedDeliveryKind.Retry)]
    [InlineData(403, SessionEndedDeliveryKind.Retry)]
    public async Task A_refused_key_is_retried_because_the_receiver_may_simply_not_have_it_yet(int status, SessionEndedDeliveryKind kind)
    {
        _answer = (status, null, TimeSpan.Zero);
        var target = new SessionEventsOptions { Url = _base + "/api/integration/auth-backend/session-ended", Key = "wrong-key" };

        var result = await Sender().SendAsync(Event(), target, default);

        Assert.Equal(kind, result.Kind);
        Assert.Equal("http_" + status, result.Outcome);
    }

    [Fact]
    public async Task Every_attempt_of_one_event_carries_the_same_idempotency_key()
    {
        var sender = Sender();

        await sender.SendAsync(Event(), Target, default);
        _answer = (503, null, TimeSpan.Zero);
        await sender.SendAsync(Event(), Target, default);

        Assert.Equal(2, _received.Count);
        var ids = _received.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("eventId").GetString()!).Distinct().ToArray();
        Assert.Equal(["0192f0a0-0000-7000-8000-000000000001"], ids);
        Assert.All(_received, r => Assert.Equal("0192f0a0000070008000000000000001", r.Headers["X-Request-Id"]));
    }

    private static HttpSessionEndedSender SenderWith(IGoogleIdTokenSource tokens)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpSessionEndedSender.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(5));
        return new HttpSessionEndedSender(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), tokens);
    }

    private sealed class CountingTokens : IGoogleIdTokenSource
    {
        public int Asked { get; private set; }

        public Task<string?> GetAsync(string audience, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult<string?>("should-not-be-used");
        }
    }

    private sealed class StaticTokens(string? token) : IGoogleIdTokenSource
    {
        public Task<string?> GetAsync(string audience, CancellationToken cancellationToken) => Task.FromResult(token);
    }
}
