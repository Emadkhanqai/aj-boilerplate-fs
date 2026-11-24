using System.Security.Claims;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Domain.AuthenticationHistory;
using AjBoilerplate.Domain.Common;
using AjBoilerplate.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AjBoilerplate.UnitTests.AuthenticationHistory;

public sealed class AuthenticationEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_sign_in_carries_the_subject_the_session_the_gateway_start_time_and_when_it_was_seen()
    {
        var started = Now.AddMinutes(-3);

        var signIn = AuthenticationEvent.Record(Guid.NewGuid(), " oid-1 ", "abc123-DEF_456", started, Now);

        Assert.Equal("oid-1", signIn.Subject);
        Assert.Equal("abc123-DEF_456", signIn.SessionId);
        Assert.Equal(started, signIn.StartedAt);
        Assert.Equal(Now, signIn.RecordedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("new\nline")]
    public void A_session_id_that_is_not_plain_token_characters_is_rejected(string sessionId) =>
        Assert.Throws<DomainException>(() => AuthenticationEvent.Record(Guid.NewGuid(), "oid-1", sessionId, Now, Now));

    [Fact]
    public void A_session_id_longer_than_the_column_is_rejected() =>
        Assert.False(AuthenticationEvent.IsSessionId(new string('a', AuthenticationEvent.MaxSessionIdLength + 1)));

    [Fact]
    public void A_missing_subject_is_rejected() =>
        Assert.Throws<DomainException>(() => AuthenticationEvent.Record(Guid.NewGuid(), "  ", "abc", Now, Now));
}

public sealed class SignInRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_new_session_is_saved_with_the_clock_time_as_the_recorded_time()
    {
        var repository = new FakeAuthenticationEventRepository();
        var recorder = new SignInRecorder(repository, new FixedClock(Now));

        var recorded = await recorder.RecordAsync("oid-1", "s1", Now.AddMinutes(-1), CancellationToken.None);

        Assert.True(recorded);
        var saved = Assert.Single(repository.Saved);
        Assert.Equal(("oid-1", "s1", Now.AddMinutes(-1), Now), (saved.Subject, saved.SessionId, saved.StartedAt, saved.RecordedAt));
    }

    [Fact]
    public async Task A_session_the_database_already_holds_is_reported_as_not_recorded()
    {
        var repository = new FakeAuthenticationEventRepository { AlreadyExists = true };

        Assert.False(await new SignInRecorder(repository, new FixedClock(Now))
            .RecordAsync("oid-1", "s1", Now, CancellationToken.None));
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task A_failure_propagates_so_the_caller_can_log_it()
    {
        var repository = new FakeAuthenticationEventRepository { Failure = new TimeoutException("db down") };

        await Assert.ThrowsAsync<TimeoutException>(() => new SignInRecorder(repository, new FixedClock(Now))
            .RecordAsync("oid-1", "s1", Now, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "s1")]
    [InlineData("oid-1", "")]
    [InlineData("oid-1", "bad id")]
    public async Task A_malformed_sign_in_records_nothing(string subject, string sessionId)
    {
        var repository = new FakeAuthenticationEventRepository();

        Assert.False(await new SignInRecorder(repository, new FixedClock(Now))
            .RecordAsync(subject, sessionId, Now, CancellationToken.None));
        Assert.Empty(repository.Attempted);
    }
}

public sealed class SignInRecordingMiddlewareTests
{
    private const string StartedAt = "2026-11-01T08:55:00Z";

    [Fact]
    public async Task An_authenticated_api_request_with_both_headers_records_once_per_session()
    {
        var recorder = new FakeRecorder();
        var cache = new MemoryCache(new MemoryCacheOptions());

        for (var i = 0; i < 3; i++)
        {
            await Invoke(recorder, cache, Context("/api/v1/me", "sess-1", StartedAt, authenticated: true));
        }

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(("oid-1", "sess-1"), (call.Subject, call.SessionId));
        Assert.Equal(DateTimeOffset.Parse(StartedAt, System.Globalization.CultureInfo.InvariantCulture), call.StartedAt);
    }

    [Fact]
    public async Task A_second_session_is_recorded_separately()
    {
        var recorder = new FakeRecorder();
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Invoke(recorder, cache, Context("/api/v1/me", "sess-1", StartedAt, authenticated: true));
        await Invoke(recorder, cache, Context("/api/v1/me", "sess-2", StartedAt, authenticated: true));

        Assert.Equal(["sess-1", "sess-2"], recorder.Calls.Select(c => c.SessionId));
    }

    [Theory]
    [InlineData("/api/v1/me", null, StartedAt, true)]
    [InlineData("/api/v1/me", "sess-1", null, true)]
    [InlineData("/api/v1/me", "sess-1", "not a date", true)]
    [InlineData("/api/v1/me", "sess-1", StartedAt, false)]
    [InlineData("/health/live", "sess-1", StartedAt, true)]
    [InlineData("/apiary", "sess-1", StartedAt, true)]
    public async Task Anything_short_of_an_authenticated_api_request_with_both_valid_headers_records_nothing(
        string path, string? sessionId, string? startedAt, bool authenticated)
    {
        var recorder = new FakeRecorder();

        await Invoke(recorder, new MemoryCache(new MemoryCacheOptions()), Context(path, sessionId, startedAt, authenticated));

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task A_recording_failure_never_fails_the_request_and_is_retried_on_the_next_one()
    {
        var recorder = new FakeRecorder { Failure = new TimeoutException("db down") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var next = 0;

        await Invoke(recorder, cache, Context("/api/v1/me", "sess-1", StartedAt, authenticated: true), () => next++);
        recorder.Failure = null;
        await Invoke(recorder, cache, Context("/api/v1/me", "sess-1", StartedAt, authenticated: true), () => next++);

        Assert.Equal(2, next);
        Assert.Equal(2, recorder.Calls.Count);
    }

    [Fact]
    public async Task The_request_always_continues_down_the_pipeline()
    {
        var next = 0;

        await Invoke(new FakeRecorder(), new MemoryCache(new MemoryCacheOptions()), Context("/health/live", null, null, false), () => next++);

        Assert.Equal(1, next);
    }

    private static Task Invoke(FakeRecorder recorder, IMemoryCache cache, HttpContext context, Action? onNext = null) =>
        new SignInRecordingMiddleware(_ =>
        {
            onNext?.Invoke();
            return Task.CompletedTask;
        }).InvokeAsync(context, cache, recorder, NullLogger<SignInRecordingMiddleware>.Instance);

    private static DefaultHttpContext Context(string path, string? sessionId, string? startedAt, bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (sessionId is not null)
        {
            context.Request.Headers[SignInRecordingMiddleware.SessionIdHeader] = sessionId;
        }

        if (startedAt is not null)
        {
            context.Request.Headers[SignInRecordingMiddleware.SessionStartedAtHeader] = startedAt;
        }

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "oid-1")], "TestBearer"));
        }

        return context;
    }

    private sealed class FakeRecorder : ISignInRecorder
    {
        public List<(string Subject, string SessionId, DateTimeOffset StartedAt)> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public Task<bool> RecordAsync(string subject, string sessionId, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            Calls.Add((subject, sessionId, startedAt));
            return Failure is null ? Task.FromResult(true) : Task.FromException<bool>(Failure);
        }
    }
}

public sealed class RateLimitPartitionTests
{
    [Fact]
    public void Two_different_users_get_two_partitions_and_one_user_keeps_one()
    {
        var a = RateLimiting.PartitionKey(Authenticated("user-a"));
        var b = RateLimiting.PartitionKey(Authenticated("user-b"));

        Assert.NotEqual(a, b);
        Assert.Equal(a, RateLimiting.PartitionKey(Authenticated("user-a")));
        Assert.Equal("user:user-a", a);
    }

    [Fact]
    public void An_anonymous_caller_is_partitioned_by_address_and_cannot_collide_with_a_user()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");

        Assert.Equal("ip:10.1.2.3", RateLimiting.PartitionKey(context));
        Assert.NotEqual(RateLimiting.PartitionKey(context), RateLimiting.PartitionKey(Authenticated("10.1.2.3")));
    }

    private static DefaultHttpContext Authenticated(string sub)
    {
        var context = new DefaultHttpContext();
        // The shape a Keycloak principal has after inbound mapping: sub becomes NameIdentifier, and
        // there is no ClaimTypes.Name, which is why Identity.Name cannot partition anyone.
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sub)], "TestBearer"));
        return context;
    }
}
