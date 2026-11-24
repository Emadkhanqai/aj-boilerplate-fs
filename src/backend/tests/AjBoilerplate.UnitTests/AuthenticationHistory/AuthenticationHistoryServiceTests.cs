using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Domain.AuthenticationHistory;
using AjBoilerplate.UnitTests.Support;
using FluentValidation;

namespace AjBoilerplate.UnitTests.AuthenticationHistory;

public sealed class AuthenticationHistorySortTests
{
    [Fact]
    public void Nothing_given_is_signed_in_newest_first()
    {
        Assert.True(AuthenticationHistorySort.TryParse(null, null, out var field, out var descending));
        Assert.Equal((AuthenticationHistorySortField.SignedIn, true), (field, descending));
    }

    [Fact]
    public void A_column_alone_is_ascending_and_a_direction_overrides_it()
    {
        Assert.True(AuthenticationHistorySort.TryParse("duration", null, out var field, out var descending));
        Assert.Equal((AuthenticationHistorySortField.Duration, false), (field, descending));

        Assert.True(AuthenticationHistorySort.TryParse("USER", "DESC", out field, out descending));
        Assert.Equal((AuthenticationHistorySortField.User, true), (field, descending));
    }

    [Fact]
    public void Every_published_key_maps_to_its_own_field()
    {
        var fields = AuthenticationHistorySort.Keys.Select(key =>
        {
            Assert.True(AuthenticationHistorySort.TryParse(key, "asc", out var field, out _));
            return field;
        }).ToList();

        Assert.Equal(Enum.GetValues<AuthenticationHistorySortField>().Length, fields.Distinct().Count());
    }

    [Theory]
    [InlineData("eventType", null)]
    [InlineData("user", "sideways")]
    [InlineData(null, "sideways")]
    public void An_unknown_key_or_direction_is_refused_never_replaced(string? sortBy, string? sortDir) =>
        Assert.False(AuthenticationHistorySort.TryParse(sortBy, sortDir, out _, out _));
}

public sealed class AuthenticationHistoryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid EventId = Guid.Parse("0192f0a0-0000-7000-8000-000000000001");

    private readonly FakeAuthenticationEventRepository _repository = new();

    private AuthenticationHistoryService Service() =>
        new(_repository, new AuthenticationHistoryQueryValidator());

    private static AuthenticationHistoryQuery Query(
        string? userId = null, string? sortBy = null, string? sortDir = null, int page = 1, int pageSize = 20,
        DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(userId, from, to, sortBy, sortDir, page, pageSize);

    [Fact]
    public async Task A_list_hands_the_repository_the_parsed_sort_and_a_trimmed_user()
    {
        await Service().ListAsync(Query(userId: " oid-1 ", sortBy: "sessionEnded", sortDir: "desc", page: 3, pageSize: 50), CancellationToken.None);

        Assert.Equal(
            new AuthenticationHistoryFilter("oid-1", null, null, AuthenticationHistorySortField.SessionEnded, true, 3, 50),
            _repository.LastFilter);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task A_page_or_page_size_out_of_range_is_a_validation_error_not_a_clamp(int page, int pageSize) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().ListAsync(Query(page: page, pageSize: pageSize), CancellationToken.None));

    [Fact]
    public async Task An_unknown_sort_column_and_an_inverted_date_window_are_validation_errors()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().ListAsync(Query(sortBy: "eventType"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().ListAsync(Query(from: Now, to: Now.AddDays(-1)), CancellationToken.None));
    }

    [Fact]
    public async Task An_open_session_has_no_end_reason_or_duration_and_carries_the_name_it_signed_in_with()
    {
        _repository.Listed.Add(AuthenticationEvent.Record(Guid.NewGuid(), "oid-1", "open", Now, Now, "Ada"));

        var page = await Service().ListAsync(Query(), CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal(("Ada", "Success", null, null, null), (row.UserDisplayName, row.Result, row.EndReason, row.EndedAtUtc, row.DurationSeconds));
        Assert.Equal(1, page.Total);
    }

    [Theory]
    [InlineData(SessionEndReasons.LoggedOut, "Success")]
    [InlineData(SessionEndReasons.SessionExpired, "Success")]
    [InlineData(SessionEndReasons.SignedOutBySystem, "Failure")]
    public void The_result_of_an_ending_follows_the_reason(string reason, string expected)
    {
        var row = EndedRow(reason, Now.AddMinutes(5));

        Assert.Equal(expected, row.Result);
    }

    [Fact]
    public async Task The_duration_is_whole_seconds_from_sign_in_to_ending()
    {
        _repository.Listed.Add(EndedRow(SessionEndReasons.LoggedOut, Now.AddMinutes(90)));

        var dto = Assert.Single((await Service().ListAsync(Query(), CancellationToken.None)).Items);

        Assert.Equal(5400L, dto.DurationSeconds);
        Assert.Equal(Now.AddMinutes(90), dto.EndedAtUtc);
    }

    private static AuthenticationEvent EndedRow(string reason, DateTimeOffset endedAt)
    {
        var row = AuthenticationEvent.Record(Guid.NewGuid(), "oid-1", "s1", Now, Now);

        // The repository's conditional UPDATE writes these in the database; here the same values are set directly.
        typeof(AuthenticationEvent).GetProperty(nameof(AuthenticationEvent.EndedAt))!.SetValue(row, endedAt);
        typeof(AuthenticationEvent).GetProperty(nameof(AuthenticationEvent.EndReason))!.SetValue(row, reason);
        return row;
    }

    // ----- the session-ended receiver ---------------------------------------------------------

    private static SessionEndedNotice Notice(
        int? schema = 1, string? type = "session.ended", string? eventId = "0192f0a0-0000-7000-8000-000000000001",
        string? session = "0123456789abcdef0123456789abcdef", string? reason = "session-expired",
        string? occurred = "2026-11-24T09:15:42Z", string? client = "boilerplate") =>
        new(schema, type, eventId, session, reason, "idle_timeout", occurred, client);

    [Fact]
    public async Task A_good_event_is_recorded_with_the_wire_values()
    {
        var result = await Service().RecordSessionEndedAsync(Notice(), "boilerplate", CancellationToken.None);

        Assert.Equal(SessionEndedOutcome.Recorded, result.Outcome);
        var end = Assert.Single(_repository.Ends);
        Assert.Equal(("0123456789abcdef0123456789abcdef", EventId, "session-expired", "idle_timeout"), (end.SessionId, end.EventId, end.Reason, end.Detail));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 15, 42, TimeSpan.Zero), end.EndedAt);
    }

    [Theory]
    [InlineData(2, "session.ended", "0192f0a0-0000-7000-8000-000000000001", "s1", "session-expired", "2026-11-24T09:15:42Z", "boilerplate", "schema-version")]
    [InlineData(1, "session.created", "0192f0a0-0000-7000-8000-000000000001", "s1", "session-expired", "2026-11-24T09:15:42Z", "boilerplate", "event-type")]
    [InlineData(1, "session.ended", "not-a-guid", "s1", "session-expired", "2026-11-24T09:15:42Z", "boilerplate", "event-id")]
    [InlineData(1, "session.ended", "00000000-0000-0000-0000-000000000000", "s1", "session-expired", "2026-11-24T09:15:42Z", "boilerplate", "event-id")]
    [InlineData(1, "session.ended", "0192f0a0-0000-7000-8000-000000000001", "has space", "session-expired", "2026-11-24T09:15:42Z", "boilerplate", "session-id")]
    [InlineData(1, "session.ended", "0192f0a0-0000-7000-8000-000000000001", "s1", "kicked", "2026-11-24T09:15:42Z", "boilerplate", "reason")]
    [InlineData(1, "session.ended", "0192f0a0-0000-7000-8000-000000000001", "s1", "session-expired", "yesterday-ish", "boilerplate", "occurred-at")]
    [InlineData(1, "session.ended", "0192f0a0-0000-7000-8000-000000000001", "s1", "session-expired", "2026-11-24T09:15:42Z", "another-app", "portal-client-id")]
    public async Task Each_check_that_makes_an_event_malformed_for_good_names_itself_and_writes_nothing(
        int schema, string type, string eventId, string session, string reason, string occurred, string client, string expectedSlug)
    {
        var result = await Service().RecordSessionEndedAsync(
            Notice(schema, type, eventId, session, reason, occurred, client), "boilerplate", CancellationToken.None);

        Assert.Equal((SessionEndedOutcome.Invalid, expectedSlug), (result.Outcome, result.InvalidReason));
        Assert.Empty(_repository.Ends);
    }

    [Fact]
    public async Task Missing_fields_reach_the_rules_instead_of_blowing_up()
    {
        var result = await Service().RecordSessionEndedAsync(
            new SessionEndedNotice(null, null, null, null, null, null, null, null), "boilerplate", CancellationToken.None);

        Assert.Equal(("schema-version", SessionEndedOutcome.Invalid), (result.InvalidReason, result.Outcome));
    }

    [Fact]
    public async Task A_blank_expected_client_does_not_check_the_portal_client_id()
    {
        var result = await Service().RecordSessionEndedAsync(Notice(client: null), "  ", CancellationToken.None);

        Assert.Equal(SessionEndedOutcome.Recorded, result.Outcome);
    }

    [Theory]
    [InlineData(SessionEndWrite.AlreadyEnded, SessionEndedOutcome.Duplicate)]
    [InlineData(SessionEndWrite.UnknownSession, SessionEndedOutcome.IgnoredUnknownSession)]
    public async Task What_the_repository_found_decides_duplicate_or_ignored_and_neither_is_an_error(
        SessionEndWrite found, SessionEndedOutcome expected)
    {
        _repository.EndResult = found;

        var result = await Service().RecordSessionEndedAsync(Notice(), "boilerplate", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task An_unrecognised_reason_detail_is_dropped_by_the_repository_never_a_reason_to_refuse()
    {
        var result = await Service().RecordSessionEndedAsync(
            Notice() with { ReasonDetail = "Free Text With Spaces" }, "boilerplate", CancellationToken.None);

        Assert.Equal(SessionEndedOutcome.Recorded, result.Outcome);
        Assert.False(AuthenticationEvent.IsSlug("Free Text With Spaces"));
        Assert.True(AuthenticationEvent.IsSlug("idle_timeout"));
    }
}
