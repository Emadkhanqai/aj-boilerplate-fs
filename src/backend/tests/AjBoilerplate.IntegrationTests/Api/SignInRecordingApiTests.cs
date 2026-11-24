using System.Net;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Domain.AuthenticationHistory;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// One sign-in per gateway session, against a real SQL Server: the unique index on the session id is
/// what decides a race, and the SQL error-number mapping that turns a duplicate into "already
/// recorded" cannot be exercised without the real engine.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SignInRecordingApiTests
{
    private const string StartedAt = "2026-11-01T08:55:00Z";

    private readonly SqlServerFixture _fixture;

    public SignInRecordingApiTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Many_requests_in_one_session_record_exactly_one_sign_in()
    {
        var session = NewSession();
        var client = Client(session, "oid-signin-1");

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", CancellationToken.None)).StatusCode);
        }

        var row = Assert.Single(await RowsAsync(session));
        Assert.Equal("oid-signin-1", row.Subject);
        Assert.Equal(DateTimeOffset.Parse(StartedAt, System.Globalization.CultureInfo.InvariantCulture), row.StartedAt);
        Assert.True(row.RecordedAt >= row.StartedAt);
    }

    [Fact]
    public async Task A_second_session_of_the_same_person_is_a_second_sign_in()
    {
        var first = NewSession();
        var second = NewSession();

        await Client(first, "oid-signin-2").GetAsync("/api/v1/me", CancellationToken.None);
        await Client(second, "oid-signin-2").GetAsync("/api/v1/me", CancellationToken.None);

        Assert.Single(await RowsAsync(first));
        Assert.Single(await RowsAsync(second));
    }

    [Fact]
    public async Task Concurrent_first_requests_of_one_session_still_record_one_row_and_none_fails()
    {
        // The in-memory memo cannot decide this: every one of these misses it at once. The unique
        // index does, and the losers must come back as 200s, not 500s.
        var session = NewSession();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            (await Client(session, "oid-signin-race").GetAsync("/api/v1/me", CancellationToken.None)).StatusCode));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Single(await RowsAsync(session));
    }

    [Theory]
    [InlineData(null, StartedAt)]
    [InlineData("has space", StartedAt)]
    [InlineData("valid-id", null)]
    [InlineData("valid-id", "yesterday-ish")]
    public async Task A_missing_or_malformed_header_records_nothing_and_never_fails_the_request(string? session, string? startedAt)
    {
        var id = session == "valid-id" ? NewSession() : session;
        var client = _fixture.Api.CreateClientAs(ApplicationRoles.Viewer, "oid-signin-bad");
        AddHeaders(client, id, startedAt);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", CancellationToken.None)).StatusCode);
        if (id is not null)
        {
            Assert.Empty(await RowsAsync(id));
        }
    }

    [Fact]
    public async Task An_unauthenticated_request_with_the_headers_records_nothing()
    {
        var session = NewSession();
        var client = _fixture.Api.CreateClient();
        AddHeaders(client, session, StartedAt);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", CancellationToken.None)).StatusCode);
        Assert.Empty(await RowsAsync(session));
    }

    [Fact]
    public async Task A_caller_the_policy_refuses_records_nothing()
    {
        var session = NewSession();
        var client = Client(session, "oid-signin-forbidden", "superuser");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/items", CancellationToken.None)).StatusCode);
        Assert.Empty(await RowsAsync(session));
    }

    private HttpClient Client(string session, string user, string role = ApplicationRoles.Viewer)
    {
        var client = _fixture.Api.CreateClientAs(role, user);
        AddHeaders(client, session, StartedAt);
        return client;
    }

    private static void AddHeaders(HttpClient client, string? session, string? startedAt)
    {
        if (session is not null)
        {
            client.DefaultRequestHeaders.Add("X-Usp-Session-Id", session);
        }

        if (startedAt is not null)
        {
            client.DefaultRequestHeaders.Add("X-Usp-Session-Started-At", startedAt);
        }
    }

    private static string NewSession() => Guid.NewGuid().ToString("N");

    private async Task<List<AuthenticationEvent>> RowsAsync(string session)
    {
        var rows = new List<AuthenticationEvent>();
        await _fixture.WithDbContextAsync(async db =>
            rows.AddRange(await db.AuthenticationEvents.AsNoTracking().Where(e => e.SessionId == session).ToListAsync()));
        return rows;
    }
}
