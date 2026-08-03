using System.Net;
using System.Net.Http.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Contracts.Features;
using AjBoilerplate.Domain.Features;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// End-to-end coverage of <c>/api/v1/features</c> against a real SQL Server: the lookup, the
/// dismissal, and the two guarantees only a database can demonstrate — the unique index that makes a
/// dismissal un-duplicable, and the cascade that removes acknowledgements with their announcement.
///
/// The database is shared across the whole integration collection, so every test seeds announcements
/// under a UNIQUE path prefix, acts as a UNIQUE user, and asserts only about the keys it created
/// itself. That is what keeps it independent of rows other tests leave behind — including the one
/// deliberately bound to every route.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FeaturesApiTests
{
    private static readonly DateTimeOffset Seeded = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public FeaturesApiTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Anonymous_requests_are_rejected_with_an_enveloped_401()
    {
        var client = _fixture.Api.CreateClient();

        var lookup = await client.GetAsync("/api/v1/features/unack?path=/", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, lookup.StatusCode);

        var envelope = await lookup.Content.ReadErrorEnvelopeAsync();
        Assert.False(envelope.IsSuccess);
        Assert.Equal(EnvelopeCodes.Unauthorized, envelope.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));

        var dismiss = await AcknowledgeAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, dismiss.StatusCode);
    }

    [Fact]
    public async Task An_announcement_bound_to_the_current_path_is_returned()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));

        var announcement = Assert.Single(
            await GetUnacknowledgedAsync(client, $"{scope}/detail"), a => a.Id == seeded.Id);

        Assert.Equal(seeded.Key, announcement.Key);
        Assert.Equal($"{seeded.Key} title", announcement.TitleEn);
        Assert.Equal($"{seeded.Key} body", announcement.BodyEn);
        // Both translations survive the whole round trip, unmangled.
        Assert.Equal($"{seeded.Key} عنوان", announcement.TitleAr);
        Assert.Equal($"{seeded.Key} نص", announcement.BodyAr);
        Assert.Equal(Seeded, announcement.CreatedAt);
    }

    [Fact]
    public async Task An_announcement_bound_elsewhere_is_not_returned()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));

        await AssertNotListedAsync(client, "/somewhere-else", seeded.Id);
    }

    [Fact]
    public async Task An_empty_page_list_matches_every_route()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, FeatureAnnouncement.EveryPage);

        await AssertListedAsync(client, "/", seeded.Id);
        await AssertListedAsync(client, "/anywhere/at/all", seeded.Id);
    }

    [Fact]
    public async Task A_path_that_resolves_elsewhere_does_not_match_the_prefix_it_appears_to_start_with()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));

        // The raw string starts with the registered prefix; the path it actually resolves to does
        // not, and the resolved path is what the comparison sees.
        await AssertNotListedAsync(client, $"{scope}/../elsewhere", seeded.Id);
        // The honest case the guard must not break.
        await AssertListedAsync(client, $"{scope}/detail/../summary", seeded.Id);
    }

    [Fact]
    public async Task A_query_string_does_not_affect_matching()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));

        await AssertListedAsync(client, $"{scope}?tab=1#section", seeded.Id);
    }

    [Fact]
    public async Task A_retired_announcement_is_never_returned()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope), isActive: false);

        await AssertNotListedAsync(client, scope, seeded.Id);
    }

    [Fact]
    public async Task Several_pending_announcements_come_back_in_display_order()
    {
        var (client, scope) = NewCaller();
        var third = await SeedAsync(scope, Bound(scope), displayOrder: 2);
        var second = await SeedAsync(scope, Bound(scope), displayOrder: 1, createdAt: Seeded.AddHours(1));
        var first = await SeedAsync(scope, Bound(scope), displayOrder: 1, createdAt: Seeded);

        var mine = new[] { first.Id, second.Id, third.Id };
        var returned = (await GetUnacknowledgedAsync(client, scope))
            .Select(a => a.Id)
            .Where(mine.Contains)
            .ToArray();

        // A DisplayOrder tie breaks by CreatedAt, so "first" precedes "second" despite being seeded
        // after it.
        Assert.Equal(mine, returned);
    }

    [Fact]
    public async Task An_acknowledged_announcement_never_comes_back()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));
        await AssertListedAsync(client, scope, seeded.Id);

        var dismiss = await AcknowledgeAsync(client, seeded.Id);

        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
        // 204 means no content — an envelope here would violate the HTTP contract.
        Assert.Empty(await dismiss.Content.ReadAsByteArrayAsync(CancellationToken.None));

        await AssertNotListedAsync(client, scope, seeded.Id);
    }

    [Fact]
    public async Task Acknowledging_twice_succeeds_and_writes_one_row()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));

        Assert.Equal(HttpStatusCode.NoContent, (await AcknowledgeAsync(client, seeded.Id)).StatusCode);
        // The retry a double-click or a flaky network produces. It must be a success, not a conflict —
        // and it must not reach the unique index at all.
        Assert.Equal(HttpStatusCode.NoContent, (await AcknowledgeAsync(client, seeded.Id)).StatusCode);

        Assert.Equal(1, await CountAcknowledgementsAsync(seeded.Id));
    }

    [Fact]
    public async Task Two_users_acknowledge_independently()
    {
        var scope = NewScope();
        var alice = _fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-a-{Guid.NewGuid():N}");
        var bob = _fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-b-{Guid.NewGuid():N}");
        var seeded = await SeedAsync(scope, Bound(scope));

        await AcknowledgeAsync(alice, seeded.Id);

        await AssertNotListedAsync(alice, scope, seeded.Id);
        // Acknowledgement is per user: Bob has not seen it, so he must still be shown it.
        await AssertListedAsync(bob, scope, seeded.Id);

        await AcknowledgeAsync(bob, seeded.Id);
        Assert.Equal(2, await CountAcknowledgementsAsync(seeded.Id));
    }

    [Fact]
    public async Task A_read_only_role_may_still_dismiss_an_announcement()
    {
        var scope = NewScope();
        var viewer = _fixture.Api.CreateClientAs(ApplicationRoles.Viewer, $"user-v-{Guid.NewGuid():N}");
        var seeded = await SeedAsync(scope, Bound(scope));

        await AssertListedAsync(viewer, scope, seeded.Id);

        // Dismissal writes a row about the CALLER, not about a business record — gating it behind the
        // write policy would leave a read-only user permanently unable to close the popup.
        Assert.Equal(HttpStatusCode.NoContent, (await AcknowledgeAsync(viewer, seeded.Id)).StatusCode);
        await AssertNotListedAsync(viewer, scope, seeded.Id);
    }

    [Fact]
    public async Task Acknowledging_an_unknown_id_is_a_no_op_rather_than_a_server_error()
    {
        var (client, _) = NewCaller();

        // A foreign-key violation here would be a 500 for a client doing nothing wrong — it may have
        // been handed the id moments before the announcement was deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await AcknowledgeAsync(client, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Acknowledging_an_empty_list_is_an_accepted_no_op()
    {
        var (client, _) = NewCaller();

        var response = await client.PostAsJsonAsync(
            "/api/v1/features/ack", new AcknowledgeFeaturesRequest([]), CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_id_is_rejected_with_an_enveloped_400()
    {
        var (client, _) = NewCaller();

        var response = await AcknowledgeAsync(client, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task The_database_refuses_a_duplicate_acknowledgement()
    {
        var scope = NewScope();
        var userId = $"user-dup-{Guid.NewGuid():N}";
        var seeded = await SeedAsync(scope, Bound(scope));

        await _fixture.WithDbContextAsync(async db =>
        {
            db.FeatureAcknowledgements.Add(
                FeatureAcknowledgement.Create(Guid.NewGuid(), userId, seeded.Id, Seeded));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        await _fixture.WithDbContextAsync(async db =>
        {
            // Unreachable through the API, because the service filters already-acknowledged ids
            // before inserting. But two requests CAN race past that check at the same instant, and
            // this index is the only thing that stops the second one then.
            db.FeatureAcknowledgements.Add(
                FeatureAcknowledgement.Create(Guid.NewGuid(), userId, seeded.Id, Seeded));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
        });
    }

    [Fact]
    public async Task Deleting_an_announcement_cascades_to_its_acknowledgements()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));
        await AcknowledgeAsync(client, seeded.Id);
        Assert.Equal(1, await CountAcknowledgementsAsync(seeded.Id));

        await _fixture.WithDbContextAsync(async db =>
        {
            db.FeatureAnnouncements.Remove(
                await db.FeatureAnnouncements.SingleAsync(f => f.Id == seeded.Id, CancellationToken.None));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        Assert.Equal(0, await CountAcknowledgementsAsync(seeded.Id));
    }

    [Fact]
    public async Task Retiring_an_announcement_hides_it_without_deleting_its_history()
    {
        var (client, scope) = NewCaller();
        var seeded = await SeedAsync(scope, Bound(scope));
        await AcknowledgeAsync(client, seeded.Id);

        await _fixture.WithDbContextAsync(async db =>
        {
            var announcement = await db.FeatureAnnouncements.SingleAsync(f => f.Id == seeded.Id, CancellationToken.None);
            announcement.Retire(Seeded.AddDays(1));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        // Retiring is a soft switch: the row and every acknowledgement of it survive, so "how many
        // people saw this?" is still answerable afterwards.
        Assert.Equal(1, await CountAcknowledgementsAsync(seeded.Id));
    }

    private static string NewScope() => $"/scope-{Guid.NewGuid():N}";

    private static string Bound(string scope) => $"[\"{scope}\"]";

    private (HttpClient Client, string Scope) NewCaller() =>
        (_fixture.Api.CreateClientAs(ApplicationRoles.Editor, $"user-{Guid.NewGuid():N}"), NewScope());

    private async Task<(Guid Id, string Key)> SeedAsync(
        string scope,
        string pagesJson,
        bool isActive = true,
        int displayOrder = 0,
        DateTimeOffset? createdAt = null)
    {
        var id = Guid.NewGuid();
        var key = $"k-{Guid.NewGuid():N}";

        await _fixture.WithDbContextAsync(async db =>
        {
            db.FeatureAnnouncements.Add(FeatureAnnouncement.Create(
                id,
                key,
                $"{key} title",
                $"{key} عنوان",
                $"{key} body",
                $"{key} نص",
                pagesJson,
                isActive,
                displayOrder,
                createdAt ?? Seeded));
            await db.SaveChangesAsync(CancellationToken.None);
        });

        return (id, key);
    }

    private async Task<int> CountAcknowledgementsAsync(Guid featureId)
    {
        var count = 0;
        await _fixture.WithDbContextAsync(async db =>
            count = await db.FeatureAcknowledgements.CountAsync(a => a.FeatureId == featureId, CancellationToken.None));
        return count;
    }

    private static async Task AssertListedAsync(HttpClient client, string path, Guid featureId) =>
        Assert.Contains(await GetUnacknowledgedAsync(client, path), a => a.Id == featureId);

    private static async Task AssertNotListedAsync(HttpClient client, string path, Guid featureId) =>
        Assert.DoesNotContain(await GetUnacknowledgedAsync(client, path), a => a.Id == featureId);

    private static async Task<IReadOnlyList<FeatureAnnouncementResponse>> GetUnacknowledgedAsync(
        HttpClient client, string path)
    {
        var response = await client.GetAsync(
            $"/api/v1/features/unack?path={Uri.EscapeDataString(path)}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadEnvelopeAsync<List<FeatureAnnouncementResponse>>();
    }

    private static Task<HttpResponseMessage> AcknowledgeAsync(HttpClient client, Guid featureId) =>
        client.PostAsJsonAsync(
            "/api/v1/features/ack", new AcknowledgeFeaturesRequest([featureId]), CancellationToken.None);
}
