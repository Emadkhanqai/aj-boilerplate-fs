using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// The portal hand-off (docs/auth/handoff.md): portal #0 (<see cref="TestConfig.Host"/>) is the SOURCE throughout, the second
/// portal the TARGET. Every refusal must be <see cref="HandoffRedemptionStatus.Fallback"/>
/// (the ordinary sign-in) and must end no session; only a definitive no-role is a 403.
/// </summary>
public sealed class HandoffServiceTests
{
    private const string SourceHost = TestConfig.Host;
    private const string SourceOrigin = TestConfig.Origin;
    private const string TargetHost = TestConfig.OtherHost;
    private const string TargetOrigin = TestConfig.OtherOrigin;
    private const string TargetClient = TestConfig.OtherClientId;
    private const string Subject = "kc-user";
    private const string RootRefreshToken = "rt-root";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly InMemorySessionStore _sessions = new();
    private readonly InMemoryHandoffTicketStore _tickets;
    private readonly FakeLock _lock = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly CapturingLogger<HandoffService> _log = new();
    private readonly HandoffService _sut;

    public HandoffServiceTests()
    {
        _tickets = new InMemoryHandoffTicketStore(_time);
        var options = TestConfig.TwoPortals();
        _sut = new HandoffService(_sessions, _tickets, _lock, _entra, _keycloak, TestConfig.Portals(options), TestConfig.Wrap(options), _time, _log);
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, Now.AddMinutes(5), TargetClient, "requester"));
    }

    private string SeedSource(DateTimeOffset? signedInAt = null, string clientId = TestConfig.ClientId, string? refreshVia = null, string? refreshToken = RootRefreshToken)
    {
        var id = SessionIds.NewRandom();
        _sessions.Sessions[SessionIds.StorageKey(id)] = new AuthSession(
            "source-jwt", Now.AddMinutes(5), refreshToken, Subject, clientId, false, signedInAt ?? Now.AddHours(-1), refreshVia);
        return id;
    }

    private async Task<string> MintAsync(string sourceId, string rd = "/items/42")
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, rd, sourceId, "same-origin", default);
        Assert.Equal(HandoffStartStatus.Ticket, start.Status);
        return start.Ticket!;
    }

    private Task<HandoffRedemption> RedeemAsync(string ticket, string? origin = SourceOrigin, string host = TargetHost, string? existing = null) =>
        _sut.RedeemAsync(host, ticket, origin, existing, default);

    private string[] Reasons() => [.. _log.Entries.Select(e => e.Field("Reason")).OfType<string>()];

    [Fact]
    public async Task Happy_path_mints_a_target_session_that_borrows_the_source_grant()
    {
        var sourceId = SeedSource(signedInAt: Now.AddHours(-3));
        var sourceKey = SessionIds.StorageKey(sourceId);

        var result = await RedeemAsync(await MintAsync(sourceId));

        Assert.Equal(HandoffRedemptionStatus.SignedIn, result.Status);
        Assert.Equal("/items/42", result.ReturnPath);
        var target = _sessions.Sessions[SessionIds.StorageKey(result.SessionId!)];
        Assert.Equal(TargetClient, target.ClientId);
        Assert.Equal(Subject, target.Subject);
        Assert.Null(target.EntraRefreshToken);
        Assert.Equal(sourceKey, target.RefreshVia);

        // The target inherits the ORIGINAL sign-in time, it does not restart the clock.
        Assert.Equal(Now.AddHours(-3), target.CreatedAt);

        // The grant: the source's token was presented, and its rotation was written back to the source.
        Assert.Equal(RootRefreshToken, _entra.LastRefreshToken);
        Assert.Equal("entra-rt-2", _sessions.Sessions[sourceKey].EntraRefreshToken);
        Assert.Equal(TargetClient, _keycloak.LastPortal!.ClientId);
        Assert.Equal([SessionValidator.LockKey(sourceKey)], _lock.Keys);
    }

    [Fact]
    public async Task A_reused_ticket_falls_back_and_never_reaches_entra_twice()
    {
        var ticket = await MintAsync(SeedSource());
        Assert.Equal(HandoffRedemptionStatus.SignedIn, (await RedeemAsync(ticket)).Status);

        var second = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, second.Status);
        Assert.Equal(1, _entra.Refreshes);
        Assert.Contains("ticket_unknown_used_or_expired", Reasons());
    }

    [Fact]
    public async Task An_expired_ticket_falls_back()
    {
        var ticket = await MintAsync(SeedSource());
        _time.Now = Now.AddSeconds(61);

        var result = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task A_ticket_presented_on_another_portal_falls_back_and_is_spent()
    {
        var ticket = await MintAsync(SeedSource());

        // A configured portal, but not the one the ticket was minted for.
        var wrongHost = await RedeemAsync(ticket, host: SourceHost);
        var rightHostAfterwards = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, wrongHost.Status);
        Assert.Equal(HandoffRedemptionStatus.Fallback, rightHostAfterwards.Status);
        Assert.Equal(0, _entra.Refreshes);
        Assert.Equal(["ticket_for_another_host", "ticket_unknown_used_or_expired"], Reasons());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("https://evil.example")]
    [InlineData("OTHER-APP")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_portal_key_mints_nothing(string? to)
    {
        var start = await _sut.BeginAsync(SourceHost, to, "/", SeedSource(), "same-origin", default);

        Assert.Equal(HandoffStartStatus.UnknownTarget, start.Status);
        Assert.Null(start.Ticket);
        Assert.Empty(_tickets.Tickets);
    }

    [Fact]
    public async Task A_crafted_portal_key_is_not_written_into_the_log()
    {
        await _sut.BeginAsync(SourceHost, "x\n{\"severity\":\"CRITICAL\"}", "/", SeedSource(), "same-origin", default);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal("<malformed>", entry.Field("PortalKey"));
    }

    [Fact]
    public async Task An_unknown_source_host_is_refused()
    {
        var start = await _sut.BeginAsync("evil.example", TargetClient, "/", SeedSource(), "same-origin", default);

        Assert.Equal(HandoffStartStatus.UnknownSource, start.Status);
        Assert.Empty(_tickets.Tickets);
    }

    [Fact]
    public async Task Handing_off_to_the_same_portal_is_a_local_redirect()
    {
        var start = await _sut.BeginAsync(SourceHost, TestConfig.ClientId, "/budgets", SeedSource(), "same-origin", default);

        Assert.Equal(HandoffStartStatus.SamePortal, start.Status);
        Assert.Equal("/budgets", start.ReturnPath);
        Assert.Empty(_tickets.Tickets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-session-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well-formed, unknown
    public async Task No_source_session_falls_back_to_the_targets_sign_in(string? cookie)
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, "/items", cookie, "same-origin", default);

        Assert.Equal(HandoffStartStatus.Fallback, start.Status);
        Assert.Equal(TargetOrigin, start.TargetOrigin);
        Assert.Equal("/items", start.ReturnPath);
        Assert.Empty(_tickets.Tickets);
    }

    [Fact]
    public async Task A_session_of_another_portal_cannot_start_a_hand_off()
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, "/", SeedSource(clientId: TargetClient), "same-origin", default);

        Assert.Equal(HandoffStartStatus.Fallback, start.Status);
        Assert.Empty(_tickets.Tickets);
    }

    [Fact]
    public async Task A_source_past_its_12_hour_absolute_lifetime_cannot_start_a_hand_off()
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, "/", SeedSource(signedInAt: Now.AddHours(-12)), "same-origin", default);

        Assert.Equal(HandoffStartStatus.Fallback, start.Status);
        Assert.Empty(_tickets.Tickets);
        Assert.Contains("source_past_absolute_lifetime", Reasons());
    }

    [Fact]
    public async Task A_source_that_crosses_its_absolute_lifetime_between_mint_and_redeem_is_refused()
    {
        var ticket = await MintAsync(SeedSource(signedInAt: Now.AddHours(-12).AddSeconds(30)));
        _time.Now = Now.AddSeconds(31);

        var result = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Equal(0, _entra.Refreshes);
        Assert.Contains("source_past_absolute_lifetime", Reasons());
    }

    [Fact]
    public async Task A_legacy_unstamped_source_gives_the_target_a_sign_in_time_of_now()
    {
        var result = await RedeemAsync(await MintAsync(SeedSource(signedInAt: DateTimeOffset.UnixEpoch)));

        Assert.Equal(Now, _sessions.Sessions[SessionIds.StorageKey(result.SessionId!)].CreatedAt);
    }

    [Fact]
    public async Task A_cross_site_start_mints_nothing()
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, "/", SeedSource(), "cross-site", default);

        Assert.Equal(HandoffStartStatus.Fallback, start.Status);
        Assert.Empty(_tickets.Tickets);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("same-site")]
    [InlineData("none")]
    [InlineData("")]
    public async Task Same_origin_same_site_typed_and_unsignalled_starts_mint(string fetchSite)
    {
        var start = await _sut.BeginAsync(SourceHost, TargetClient, "/", SeedSource(), fetchSite, default);

        Assert.Equal(HandoffStartStatus.Ticket, start.Status);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://other.example.com")]
    [InlineData("http://app.example.com")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Redeem_from_any_origin_but_the_source_falls_back_and_spends_the_ticket(string? origin)
    {
        var ticket = await MintAsync(SeedSource());

        var result = await RedeemAsync(ticket, origin);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Equal(0, _entra.Refreshes);
        Assert.Empty(_tickets.Tickets);
    }

    [Fact]
    public async Task Keycloak_no_role_is_a_403_and_ends_nothing()
    {
        var sourceId = SeedSource();
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create(Subject, Now.AddMinutes(5), TargetClient));

        var result = await RedeemAsync(await MintAsync(sourceId));

        Assert.Equal(HandoffRedemptionStatus.NoAccess, result.Status);
        Assert.Single(_sessions.Sessions);
        Assert.True(_sessions.Sessions.ContainsKey(SessionIds.StorageKey(sourceId)));
    }

    [Fact]
    public async Task Keycloak_denial_is_a_403()
    {
        _keycloak.Next = ExchangeResult.Denied;

        Assert.Equal(HandoffRedemptionStatus.NoAccess, (await RedeemAsync(await MintAsync(SeedSource()))).Status);
    }

    [Fact]
    public async Task A_token_minted_for_another_client_is_a_403()
    {
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.CreateFlat(Subject, Now.AddMinutes(5), azp: "some-other-client", "requester"));

        Assert.Equal(HandoffRedemptionStatus.NoAccess, (await RedeemAsync(await MintAsync(SeedSource()))).Status);
    }

    [Theory]
    [InlineData(EntraOutcome.InvalidGrant)]
    [InlineData(EntraOutcome.Transient)]
    public async Task An_entra_refresh_failure_falls_back_and_ends_no_session(EntraOutcome outcome)
    {
        var sourceId = SeedSource();
        _entra.NextRefresh = new EntraTokenResult(outcome);

        var result = await RedeemAsync(await MintAsync(sourceId));

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        var source = Assert.Single(_sessions.Sessions).Value;
        Assert.Equal(RootRefreshToken, source.EntraRefreshToken);
    }

    [Fact]
    public async Task A_keycloak_transient_failure_falls_back_but_keeps_the_rotated_token()
    {
        var sourceId = SeedSource();
        _keycloak.Next = ExchangeResult.Transient;

        var result = await RedeemAsync(await MintAsync(sourceId));

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Equal("entra-rt-2", _sessions.Sessions[SessionIds.StorageKey(sourceId)].EntraRefreshToken);
    }

    [Fact]
    public async Task A_source_session_signed_out_between_mint_and_redeem_falls_back()
    {
        var sourceId = SeedSource();
        var ticket = await MintAsync(sourceId);
        _sessions.Sessions.Clear();

        var result = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Contains("source_session_gone", Reasons());
    }

    [Fact]
    public async Task A_ticket_cannot_carry_a_subject_other_than_its_sessions()
    {
        var sourceId = SeedSource();
        var ticket = await MintAsync(sourceId);
        var key = SessionIds.StorageKey(sourceId);
        _sessions.Sessions[key] = _sessions.Sessions[key] with { Subject = "someone-else" };

        var result = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Contains("subject_mismatch", Reasons());
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task A_minted_token_for_a_different_person_is_refused()
    {
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("another-user", Now.AddMinutes(5), TargetClient, "requester"));

        var result = await RedeemAsync(await MintAsync(SeedSource()));

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Contains("minted_subject_mismatch", Reasons());
        Assert.Single(_sessions.Sessions);
    }

    [Fact]
    public async Task An_existing_live_target_session_of_the_same_person_is_reused()
    {
        var ticket = await MintAsync(SeedSource());
        var existing = SessionIds.NewRandom();
        _sessions.Sessions[SessionIds.StorageKey(existing)] = new AuthSession("t", Now.AddMinutes(5), "rt", Subject, TargetClient, false, Now);

        var result = await RedeemAsync(ticket, existing: existing);

        Assert.Equal(HandoffRedemptionStatus.Reused, result.Status);
        Assert.Equal(0, _entra.Refreshes);
    }

    [Fact]
    public async Task Another_persons_session_on_the_target_is_not_reused()
    {
        var ticket = await MintAsync(SeedSource());
        var existing = SessionIds.NewRandom();
        _sessions.Sessions[SessionIds.StorageKey(existing)] = new AuthSession("t", Now.AddMinutes(5), "rt", "previous-user", TargetClient, false, Now);

        var result = await RedeemAsync(ticket, existing: existing);

        Assert.Equal(HandoffRedemptionStatus.SignedIn, result.Status);
    }

    [Fact]
    public async Task A_hand_off_from_a_handed_off_session_points_at_the_original_grant()
    {
        var rootId = SeedSource();
        var rootKey = SessionIds.StorageKey(rootId);

        // A handed-off session carries RefreshVia; seed one on the source portal.
        var derivedId = SessionIds.NewRandom();
        _sessions.Sessions[SessionIds.StorageKey(derivedId)] = new AuthSession(
            "derived-jwt", Now.AddMinutes(5), null, Subject, TestConfig.ClientId, false, Now.AddHours(-1), rootKey);

        var result = await RedeemAsync(await MintAsync(derivedId));

        Assert.Equal(HandoffRedemptionStatus.SignedIn, result.Status);
        Assert.Equal(rootKey, _sessions.Sessions[SessionIds.StorageKey(result.SessionId!)].RefreshVia);
        Assert.Equal(RootRefreshToken, _entra.LastRefreshToken);
        Assert.Equal([SessionValidator.LockKey(rootKey)], _lock.Keys);
    }

    [Fact]
    public async Task A_lineage_whose_lock_stays_busy_falls_back_without_refreshing()
    {
        var ticket = await MintAsync(SeedSource());
        _lock.HeldElsewhere = true;

        var result = await RedeemAsync(ticket);

        Assert.Equal(HandoffRedemptionStatus.Fallback, result.Status);
        Assert.Equal(0, _entra.Refreshes);
        Assert.Contains("lineage_refresh_busy", Reasons());
    }

    [Fact]
    public async Task A_lineage_lock_released_mid_wait_is_acquired()
    {
        var ticket = await MintAsync(SeedSource());
        _lock.FailNext = 3;

        Assert.Equal(HandoffRedemptionStatus.SignedIn, (await RedeemAsync(ticket)).Status);
    }

    [Fact]
    public async Task The_ticket_and_its_hash_appear_in_no_log_line()
    {
        var sourceId = SeedSource();
        var tickets = new List<string>();

        // Every path that logs: issued, redeemed, reused-ticket, wrong-host, origin refusals, no-access.
        var first = await MintAsync(sourceId);
        tickets.Add(first);
        await RedeemAsync(first);
        await RedeemAsync(first);
        var second = await MintAsync(sourceId);
        tickets.Add(second);
        await RedeemAsync(second, host: SourceHost);
        var third = await MintAsync(sourceId);
        tickets.Add(third);
        await RedeemAsync(third, origin: "https://evil.example");
        _keycloak.Next = ExchangeResult.Denied;
        var fourth = await MintAsync(sourceId);
        tickets.Add(fourth);
        await RedeemAsync(fourth);

        // CONTROL: the capture is live and saw the paths above, so an absence below is a result.
        Assert.Contains("ticket_unknown_used_or_expired", Reasons());
        Assert.Contains("origin_mismatch", Reasons());
        Assert.True(_log.Entries.Count >= 8, $"captured {_log.Entries.Count} lines");

        foreach (var ticket in tickets)
        {
            LogAssert.Absent(_log.Entries, ticket);
            LogAssert.Absent(_log.Entries, SessionIds.StorageKey(ticket));
        }
    }
}

/// <summary>Search for a secret across every captured message AND every structured field.</summary>
internal static class LogAssert
{
    public static void Absent(IEnumerable<CapturedEntry> entries, string secret)
    {
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            foreach (var value in entry.Fields.Values)
            {
                Assert.DoesNotContain(secret, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }
}

/// <summary>The detector itself must be able to fail, or its passes above mean nothing.</summary>
public sealed class LogAssertControlTests
{
    [Fact]
    public void Absent_fails_when_the_secret_is_in_a_message()
    {
        var entries = new[] { new CapturedEntry(Microsoft.Extensions.Logging.LogLevel.Information, default, "leaked SECRET here", new Dictionary<string, object?>()) };

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LogAssert.Absent(entries, "SECRET"));
    }

    [Fact]
    public void Absent_fails_when_the_secret_is_only_in_a_field()
    {
        var entries = new[] { new CapturedEntry(Microsoft.Extensions.Logging.LogLevel.Information, default, "clean", new Dictionary<string, object?> { ["X"] = "SECRET" }) };

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LogAssert.Absent(entries, "SECRET"));
    }
}
