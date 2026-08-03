using System.Net;
using System.Net.Http.Json;
using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Items;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// <c>Idempotency-Key</c> end to end, against a real SQL Server — which is the only way to test it
/// honestly. The guarantee rests on a unique index rejecting a second concurrent claim, and an
/// in-memory provider that cannot enforce a unique index would let every one of these tests pass
/// while the mechanism did nothing.
///
/// Every test creates items under a name unique to itself and counts only those, because the database
/// is shared across the whole integration collection.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IdempotencyApiTests
{
    private readonly SqlServerFixture _fixture;

    public IdempotencyApiTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_request_without_a_key_is_untouched()
    {
        var (client, name) = NewCaller();

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, name)).StatusCode);
        // No key means no guarantee — two identical requests create two records, exactly as before
        // this middleware existed. The feature is opt-in and must not change unlabelled traffic.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, name)).StatusCode);

        Assert.Equal(2, await CountItemsAsync(name));
    }

    [Fact]
    public async Task A_retried_request_replays_the_first_response_and_writes_once()
    {
        var (client, name) = NewCaller();
        var key = NewKey();

        var first = await CreateAsync(client, name, key);
        var second = await CreateAsync(client, name, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        // The retry is answered with the ORIGINAL response — same status, same body, same id — rather
        // than a second create or a conflict the client would have to interpret.
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(CancellationToken.None),
            await second.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(1, await CountItemsAsync(name));
    }

    [Fact]
    public async Task A_replayed_response_says_so()
    {
        var (client, name) = NewCaller();
        var key = NewKey();

        var first = await CreateAsync(client, name, key);
        var second = await CreateAsync(client, name, key);

        Assert.False(first.Headers.Contains(IdempotencyMiddleware.ReplayedHeaderName));
        Assert.True(second.Headers.Contains(IdempotencyMiddleware.ReplayedHeaderName));
    }

    [Fact]
    public async Task A_replay_reproduces_the_location_header_too()
    {
        var (client, name) = NewCaller();
        var key = NewKey();

        var first = await CreateAsync(client, name, key);
        var second = await CreateAsync(client, name, key);

        // A 201's Location is how the client reads back what it created. Replaying the status and
        // body but dropping Location would hand a retrying client a materially different response
        // from the one the first attempt received — which is the whole thing this mechanism promises
        // not to do.
        Assert.NotNull(first.Headers.Location);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
    }

    [Fact]
    public async Task Two_callers_using_the_same_key_do_not_collide()
    {
        // The security property. A key is scoped to the authenticated caller, so one user cannot read
        // back another's stored response — nor block them — by guessing or reusing a key. "1" is a
        // realistic client bug, not a contrived one.
        var name = NewName();
        var key = NewKey();
        var alice = _fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-a-{Guid.NewGuid():N}");
        var bob = _fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-b-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(alice, name, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(bob, name, key)).StatusCode);

        Assert.Equal(2, await CountItemsAsync(name));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_payload_is_refused()
    {
        var (client, name) = NewCaller();
        var key = NewKey();

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, name, key)).StatusCode);

        var reused = await CreateAsync(client, $"{name}-different", key);

        // Replaying would silently discard this request and tell the caller its second item exists;
        // executing would break the guarantee the key asked for.
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(EnvelopeCodes.Conflict, (await reused.Content.ReadErrorEnvelopeAsync()).Code);
        Assert.Equal(1, await CountItemsAsync(name));
    }

    [Fact]
    public async Task A_failed_request_releases_its_key_so_a_retry_really_retries()
    {
        var (client, name) = NewCaller();
        var key = NewKey();

        // An empty name fails validation.
        var failed = await CreateAsync(client, string.Empty, key);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        // Same key, now with a valid payload. Storing the 400 would have frozen the key and left the
        // client permanently unable to complete the request it was retrying.
        var retried = await CreateAsync(client, name, key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(1, await CountItemsAsync(name));
    }

    [Fact]
    public async Task A_malformed_key_is_rejected_with_an_enveloped_400()
    {
        var (client, name) = NewCaller();

        var response = await CreateAsync(client, name, new string('k', IdempotencyMiddleware.MaxKeyLength + 1));

        // Rejected at the edge rather than truncated at the column, which would surface as a 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
        Assert.Equal(0, await CountItemsAsync(name));
    }

    [Fact]
    public async Task An_anonymous_request_carrying_a_key_is_still_rejected()
    {
        var response = await CreateAsync(_fixture.Api.CreateClient(), NewName(), NewKey());

        // There is no identity to scope the key to, so the middleware steps aside and authorization
        // answers. A key must never be a way around authentication.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_on_a_read_is_ignored()
    {
        var client = _fixture.Api.CreateClientAs(ApplicationRoles.Viewer, $"user-{Guid.NewGuid():N}");
        var key = NewKey();

        // GET is already safe to repeat. Storing and replaying reads would serve stale data from a
        // table that exists to protect writes.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/items");
            request.Headers.Add(IdempotencyMiddleware.HeaderName, key);
            var response = await client.SendAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains(IdempotencyMiddleware.ReplayedHeaderName));
        }

        Assert.Equal(0, await CountRecordsAsync(key));
    }

    [Fact]
    public async Task Concurrent_requests_sharing_a_key_create_exactly_one_record()
    {
        // THE CASE THE WHOLE MECHANISM EXISTS FOR, and the only one an in-memory database could not
        // prove. Every request here passes the "not claimed yet" lookup at the same instant; the
        // unique index on (Scope, Key) is the sole reason only one of them writes an item.
        //
        // The race outcome is nondeterministic by nature — the assertion is not. Whatever interleaving
        // occurs, exactly one caller may create the record, and no caller may see a 500.
        var (client, name) = NewCaller();
        var key = NewKey();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => CreateAsync(client, name, key)));

        Assert.Equal(1, await CountItemsAsync(name));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(created >= 1, "At least one concurrent caller must have been allowed through.");

        foreach (var response in responses)
        {
            // A loser is either replayed the winner's 201 or told the work is already in flight. It is
            // never a server error, and it never creates a second record.
            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected status {(int)response.StatusCode} from a concurrent idempotent create.");
        }
    }

    [Fact]
    public async Task The_stored_record_is_scoped_to_the_caller_and_holds_the_response()
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var client = _fixture.Api.CreateClientAs(ApplicationRoles.Editor, userId);
        var name = NewName();
        var key = NewKey();

        await CreateAsync(client, name, key);

        await _fixture.WithDbContextAsync(async db =>
        {
            var record = await db.IdempotencyRecords.SingleAsync(r => r.Key == key, CancellationToken.None);

            Assert.Equal(userId, record.Scope);
            Assert.Equal("POST", record.Method);
            Assert.Equal("/api/v1/items", record.Path);
            Assert.Equal((int)HttpStatusCode.Created, record.ResponseStatusCode);
            Assert.NotNull(record.ResponseBody);
            Assert.NotEmpty(record.ResponseBody!);
            Assert.NotNull(record.ResponseLocation);
            Assert.NotNull(record.CompletedAt);
        });
    }

    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    private static string NewName() => $"idem-{Guid.NewGuid():N}";

    private (HttpClient Client, string Name) NewCaller() =>
        (_fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-{Guid.NewGuid():N}"), NewName());

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/items")
        {
            Content = JsonContent.Create(new CreateItemRequest(name, null, null)),
        };

        if (key is not null)
        {
            request.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        }

        return client.SendAsync(request, CancellationToken.None);
    }

    private async Task<int> CountItemsAsync(string name)
    {
        var count = 0;
        await _fixture.WithDbContextAsync(async db =>
            count = await db.Items.CountAsync(i => i.Name == name, CancellationToken.None));
        return count;
    }

    private async Task<int> CountRecordsAsync(string key)
    {
        var count = 0;
        await _fixture.WithDbContextAsync(async db =>
            count = await db.IdempotencyRecords.CountAsync(r => r.Key == key, CancellationToken.None));
        return count;
    }
}
