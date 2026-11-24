using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.AuthenticationHistory;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Domain.AuthenticationHistory;
using AjBoilerplate.Infrastructure.Persistence;
using AjBoilerplate.IntegrationTests.Support;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// <c>GET /api/v1/admin/authentication-history</c> against a real SQL Server: the sort has to be translated to
/// SQL (a client-side sort would only order one page), and the order of an open session's NULLs is SQL Server's
/// own. Every test seeds its OWN user, or its own far-past window, because the database is shared with the rest of
/// the collection.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthenticationHistoryApiTests
{
    private const string Url = "/api/v1/admin/authentication-history";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;

    public AuthenticationHistoryApiTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_administrator_reads_the_sessions_and_the_wire_names_are_the_ones_the_screen_binds_to()
    {
        var user = NewUser();
        await SeedAsync(user, "Ada Lovelace", T(2001, 1, 1, 8), end: (T(2001, 1, 1, 8, 10), SessionEndReasons.LoggedOut, "user_logout"));

        var response = await Admin().GetAsync($"{Url}?userId={user}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var data = raw.RootElement.GetProperty("data");
        Assert.Equal(new[] { "items", "page", "pageSize", "total" }.Order(StringComparer.Ordinal), data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var row = data.GetProperty("items")[0];
        Assert.Equal(
            new[] { "durationSeconds", "endReason", "endReasonDetail", "endedAtUtc", "id", "result", "signedInAtUtc", "userDisplayName", "userId" }.Order(StringComparer.Ordinal),
            row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("Ada Lovelace", "logged-out", "user_logout", "Success", 600L), (
            row.GetProperty("userDisplayName").GetString(), row.GetProperty("endReason").GetString(),
            row.GetProperty("endReasonDetail").GetString(), row.GetProperty("result").GetString(), row.GetProperty("durationSeconds").GetInt64()));
    }

    public static TheoryData<string, string, int[]> SortCases => new()
    {
        { "signedIn", "asc", [1, 2, 3, 4] },
        { "signedIn", "desc", [4, 3, 2, 1] },
        { "sessionEnded", "asc", [3, 1, 2, 4] },
        { "sessionEnded", "desc", [4, 2, 1, 3] },
        { "duration", "asc", [3, 2, 1, 4] },
        { "duration", "desc", [4, 1, 2, 3] },
        { "result", "asc", [2, 4, 3, 1] },
        { "result", "desc", [4, 3, 1, 2] },
        { "endReason", "asc", [3, 1, 4, 2] },
        { "endReason", "desc", [2, 4, 1, 3] },
    };

    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task Every_data_column_sorts_across_the_whole_result_and_an_open_session_has_its_NULLs_where_SQL_Server_puts_them(
        string sortBy, string sortDir, int[] expectedOrder)
    {
        var user = NewUser();
        var starts = new[] { T(2002, 1, 1, 8), T(2002, 1, 1, 9), T(2002, 1, 1, 10), T(2002, 1, 1, 11) };
        await SeedAsync(user, "Sorter", starts[0], end: (starts[0].AddMinutes(10), SessionEndReasons.LoggedOut, null));
        await SeedAsync(user, "Sorter", starts[1], end: (starts[1].AddSeconds(5), SessionEndReasons.SignedOutBySystem, "keycloak_denied"));
        await SeedAsync(user, "Sorter", starts[2], end: null);
        await SeedAsync(user, "Sorter", starts[3], end: (starts[3].AddHours(1), SessionEndReasons.SessionExpired, "idle_timeout"));

        var page = await ListAsync($"userId={user}&sortBy={sortBy}&sortDir={sortDir}");

        Assert.Equal(expectedOrder.Select(n => starts[n - 1]), page.Items.Select(i => i.SignedInAtUtc));
    }

    [Fact]
    public async Task The_user_column_sorts_by_the_name_and_falls_back_to_the_subject_for_a_row_with_none()
    {
        var alice = NewUser();
        var bob = NewUser();
        var nameless = "zzz-" + NewUser();
        await SeedAsync(bob, "Bob", T(2003, 1, 1, 8), end: null);
        await SeedAsync(alice, "Alice", T(2003, 1, 1, 9), end: null);
        await SeedAsync(nameless, null, T(2003, 1, 1, 10), end: null);

        var window = "from=2003-01-01T00:00:00Z&to=2003-01-02T00:00:00Z";
        var ascending = await ListAsync($"{window}&sortBy=user&sortDir=asc");
        var descending = await ListAsync($"{window}&sortBy=user&sortDir=desc");

        Assert.Equal([alice, bob, nameless], ascending.Items.Select(i => i.UserId));
        Assert.Equal([nameless, bob, alice], descending.Items.Select(i => i.UserId));
    }

    [Fact]
    public async Task Paging_counts_sessions_and_the_pages_partition_them()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(user, "Pager", T(2004, 1, 1, 8 + i), end: null);
        }

        var first = await ListAsync($"userId={user}&pageSize=2&page=1");
        var third = await ListAsync($"userId={user}&pageSize=2&page=3");
        var beyond = await ListAsync($"userId={user}&pageSize=2&page=4");

        Assert.Equal((5, 2, 2, 1, 0), (first.Total, first.Items.Count, first.PageSize, third.Items.Count, beyond.Items.Count));
        Assert.Equal(T(2004, 1, 1, 12), first.Items[0].SignedInAtUtc);
        Assert.Equal(T(2004, 1, 1, 8), third.Items[0].SignedInAtUtc);
    }

    [Fact]
    public async Task The_date_filter_includes_from_and_excludes_to_and_the_user_filter_narrows_it()
    {
        var user = NewUser();
        var other = NewUser();
        await SeedAsync(user, "A", T(2005, 3, 1, 0), end: null);
        await SeedAsync(user, "A", T(2005, 3, 2, 0), end: null);
        await SeedAsync(other, "B", T(2005, 3, 1, 12), end: null);

        var window = await ListAsync("from=2005-03-01T00:00:00Z&to=2005-03-02T00:00:00Z");
        var mine = await ListAsync($"userId={user}&from=2005-03-01T00:00:00Z&to=2005-03-03T00:00:00Z");

        Assert.Equal([other, user], window.Items.Select(i => i.UserId));
        Assert.Equal(2, mine.Total);
        Assert.All(mine.Items, i => Assert.Equal(user, i.UserId));
    }

    [Theory]
    [InlineData("sortBy=eventType")]
    [InlineData("sortBy=user&sortDir=sideways")]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("from=2005-03-02T00:00:00Z&to=2005-03-01T00:00:00Z")]
    public async Task A_bad_query_is_an_enveloped_400_never_a_silent_default(string query)
    {
        var response = await Admin().GetAsync($"{Url}?{query}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task The_users_endpoint_names_each_person_once_with_a_name_and_a_nameless_one_by_subject()
    {
        var named = NewUser();
        var nameless = NewUser();
        await SeedAsync(named, "Zelda Users", T(2006, 1, 1, 8), end: null);
        await SeedAsync(named, "Zelda Users", T(2006, 1, 2, 8), end: null);
        await SeedAsync(nameless, null, T(2006, 1, 1, 9), end: null);

        var response = await Admin().GetAsync($"{Url}/users", CancellationToken.None);
        var users = await response.Content.ReadEnvelopeAsync<List<AuthenticationHistoryUserResponse>>();

        Assert.Single(users, u => u.UserId == named);
        Assert.Equal("Zelda Users", users.Single(u => u.UserId == named).UserDisplayName);
        Assert.Null(users.Single(u => u.UserId == nameless).UserDisplayName);
        Assert.Equal(users.Count, users.Select(u => u.UserId).Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/users")]
    public async Task Only_an_administrator_may_read_it(string suffix)
    {
        var anonymous = await _fixture.Api.CreateClient().GetAsync(Url + suffix, CancellationToken.None);
        var editor = await _fixture.Api.CreateClientAs(ApplicationRoles.Editor).GetAsync(Url + suffix, CancellationToken.None);
        var viewer = await _fixture.Api.CreateClientAs(ApplicationRoles.Viewer).GetAsync(Url + suffix, CancellationToken.None);
        var admin = await Admin().GetAsync(Url + suffix, CancellationToken.None);

        Assert.Equal(
            (HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK),
            (anonymous.StatusCode, editor.StatusCode, viewer.StatusCode, admin.StatusCode));
    }

    // ----- helpers -----------------------------------------------------------------------------

    private HttpClient Admin() => _fixture.Api.CreateClientAs(ApplicationRoles.Admin);

    private async Task<PagedResponse<AuthenticationSessionResponse>> ListAsync(string query)
    {
        var response = await Admin().GetAsync($"{Url}?{query}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadEnvelopeAsync<PagedResponse<AuthenticationSessionResponse>>();
    }

    private static string NewUser() => "hist-" + Guid.NewGuid().ToString("N");

    private static DateTimeOffset T(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private async Task SeedAsync(
        string subject, string? name, DateTimeOffset startedAt, (DateTimeOffset At, string Reason, string? Detail)? end)
    {
        var session = Guid.NewGuid().ToString("N");
        await _fixture.WithDbContextAsync(async db =>
        {
            db.AuthenticationEvents.Add(AuthenticationEvent.Record(Guid.NewGuid(), subject, session, startedAt, startedAt.AddSeconds(1), name));
            await db.SaveChangesAsync();
            if (end is { } e)
            {
                // The real write path, so the seed cannot disagree with what the receiver stores.
                await new AuthenticationEventRepository(db).EndAsync(session, Guid.NewGuid(), e.Reason, e.Detail, e.At, CancellationToken.None);
            }
        });
    }
}
