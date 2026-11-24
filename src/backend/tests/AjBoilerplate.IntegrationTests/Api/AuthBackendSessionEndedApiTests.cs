using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Domain.AuthenticationHistory;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// <c>POST /api/integration/auth-backend/session-ended</c>, end to end: the real authentication scheme, the real
/// filter that makes the surface not exist, the real conditional UPDATE. The key is never a constant in the
/// product: every test host that needs one is given it as configuration.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthBackendSessionEndedApiTests : IDisposable
{
    private const string Url = "/api/integration/auth-backend/session-ended";
    private const string Key = "integration-test-events-key";

    private readonly SqlServerFixture _fixture;
    private readonly WebApplicationFactory<Program> _configured;

    public AuthBackendSessionEndedApiTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _configured = fixture.Api.WithWebHostBuilder(b => b.UseSetting("Integration:AuthBackend:EventsKey", Key));
    }

    public void Dispose() => _configured.Dispose();

    [Fact]
    public async Task While_no_key_is_configured_the_surface_does_not_exist_whatever_is_sent()
    {
        var client = _fixture.Api.CreateClient();

        var anonymous = await client.PostAsJsonAsync(Url, Event(Guid.NewGuid().ToString("N")), CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-Auth-Events-Key", Key);
        var withAKey = await client.PostAsJsonAsync(Url, Event(Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (anonymous.StatusCode, withAKey.StatusCode));
        Assert.Equal(EnvelopeCodes.NotFound, (await withAKey.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData(Key + "x")]
    public async Task A_missing_or_wrong_key_is_401_and_writes_nothing(string? key)
    {
        var session = await SeedSessionAsync();
        var client = _configured.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Auth-Events-Key", key);
        }

        var response = await client.PostAsJsonAsync(Url, Event(session), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await RowAsync(session)).EndedAt);
    }

    [Theory]
    [InlineData(ApplicationRoles.Admin)]
    [InlineData(ApplicationRoles.Editor)]
    public async Task A_signed_in_person_even_an_administrator_cannot_use_it(string role)
    {
        var session = await SeedSessionAsync();
        var client = _configured.CreateClientAsRole(role);

        var response = await client.PostAsJsonAsync(Url, Event(session), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await RowAsync(session)).EndedAt);
    }

    [Fact]
    public async Task The_right_key_records_the_ending_on_the_sessions_row_and_answers_bare()
    {
        var session = await SeedSessionAsync();

        var response = await Sender().PostAsJsonAsync(Url, Event(session, reason: "session-expired", detail: "idle_timeout"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        // BARE: `outcome` is at the top level, with no envelope around it — the sender reads it there.
        Assert.Equal(["outcome"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("recorded", body.RootElement.GetProperty("outcome").GetString());

        var row = await RowAsync(session);
        Assert.Equal(("session-expired", "idle_timeout", "Success"), (row.EndReason, row.EndReasonDetail, row.Result));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 15, 42, TimeSpan.Zero), row.EndedAt);
    }

    [Fact]
    public async Task A_system_sign_out_makes_the_sessions_result_a_failure()
    {
        var session = await SeedSessionAsync();

        await Sender().PostAsJsonAsync(Url, Event(session, reason: "signed-out-by-system", detail: "keycloak_denied"), CancellationToken.None);

        Assert.Equal("Failure", (await RowAsync(session)).Result);
    }

    [Fact]
    public async Task A_retry_and_a_second_event_for_the_session_are_duplicates_and_the_first_ending_stands()
    {
        var session = await SeedSessionAsync();
        var first = Guid.NewGuid().ToString();

        var recorded = await OutcomeAsync(Event(session, eventId: first, reason: "logged-out"));
        var retry = await OutcomeAsync(Event(session, eventId: first, reason: "logged-out"));
        var other = await OutcomeAsync(Event(session, eventId: Guid.NewGuid().ToString(), reason: "session-expired"));

        Assert.Equal(["recorded", "duplicate", "duplicate"], [recorded, retry, other]);
        var row = await RowAsync(session);
        Assert.Equal(("logged-out", Guid.Parse(first)), (row.EndReason, row.EndEventId!.Value));
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_event_record_it_exactly_once_and_none_fails()
    {
        var session = await SeedSessionAsync();
        var body = Event(session);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => OutcomeAsync(body)));

        Assert.Equal(1, outcomes.Count(o => o == "recorded"));
        Assert.Equal(9, outcomes.Count(o => o == "duplicate"));
    }

    [Fact]
    public async Task A_session_nobody_recorded_is_ignored_with_a_200_not_an_error()
    {
        var unknown = Guid.NewGuid().ToString("N");

        Assert.Equal("ignored-unknown-session", await OutcomeAsync(Event(unknown)));
        await _fixture.WithDbContextAsync(async db =>
            Assert.False(await db.AuthenticationEvents.AnyAsync(e => e.SessionId == unknown)));
    }

    [Theory]
    [InlineData("schemaVersion", 2)]
    [InlineData("eventType", "session.created")]
    [InlineData("eventId", "not-a-guid")]
    [InlineData("sessionId", "has space")]
    [InlineData("reason", "kicked")]
    [InlineData("occurredAtUtc", "yesterday-ish")]
    public async Task An_event_that_can_never_be_valid_is_a_400_so_the_sender_abandons_it(string field, object value)
    {
        var session = await SeedSessionAsync();
        var body = Event(session);
        body[field] = value;

        var response = await Sender().PostAsJsonAsync(Url, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await RowAsync(session)).EndedAt);
    }

    [Fact]
    public async Task A_body_with_fields_missing_reaches_the_rules_as_a_400_not_a_binding_failure_of_another_kind()
    {
        var response = await Sender().PostAsJsonAsync(Url, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task The_surface_is_not_a_sign_in_and_records_none()
    {
        var session = await SeedSessionAsync();
        var client = Sender();
        client.DefaultRequestHeaders.Add("X-Usp-Session-Id", "machine-" + Guid.NewGuid().ToString("N"));
        client.DefaultRequestHeaders.Add("X-Usp-Session-Started-At", "2026-11-24T08:00:00Z");
        var before = await CountAsync();

        await client.PostAsJsonAsync(Url, Event(session), CancellationToken.None);

        Assert.Equal(before, await CountAsync());
    }

    // ----- helpers -----------------------------------------------------------------------------

    private HttpClient Sender()
    {
        var client = _configured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Auth-Events-Key", Key);
        return client;
    }

    private async Task<string> OutcomeAsync(Dictionary<string, object?> body)
    {
        var response = await Sender().PostAsJsonAsync(Url, body, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("outcome").GetString()!;
    }

    private static Dictionary<string, object?> Event(
        string session, string? eventId = null, string reason = "logged-out", string detail = "user_logout") => new()
        {
            ["schemaVersion"] = 1,
            ["eventType"] = "session.ended",
            ["eventId"] = eventId ?? Guid.NewGuid().ToString(),
            ["sessionId"] = session,
            ["reason"] = reason,
            ["reasonDetail"] = detail,
            ["occurredAtUtc"] = "2026-11-24T09:15:42Z",
            ["sessionStartedAtUtc"] = "2026-11-24T08:00:00Z",
            ["identityProvider"] = "Auth Backend (Keycloak)",
            ["portalClientId"] = null,
            ["correlationId"] = null,
        };

    private async Task<string> SeedSessionAsync()
    {
        var session = Guid.NewGuid().ToString("N");
        await _fixture.WithDbContextAsync(async db =>
        {
            db.AuthenticationEvents.Add(AuthenticationEvent.Record(
                Guid.NewGuid(), "oid-ended-" + session, session, new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });
        return session;
    }

    private async Task<AuthenticationEvent> RowAsync(string session)
    {
        AuthenticationEvent? row = null;
        await _fixture.WithDbContextAsync(async db =>
            row = await db.AuthenticationEvents.AsNoTracking().SingleAsync(e => e.SessionId == session));
        return row!;
    }

    private async Task<int> CountAsync()
    {
        var count = 0;
        await _fixture.WithDbContextAsync(async db => count = await db.AuthenticationEvents.CountAsync());
        return count;
    }
}
