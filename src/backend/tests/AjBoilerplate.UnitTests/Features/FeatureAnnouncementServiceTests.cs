using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Features;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.UnitTests.Support;
using FluentValidation;

namespace AjBoilerplate.UnitTests.Features;

/// <summary>
/// The "what's new" use cases: who sees what, in what order, and the idempotent dismissal that must
/// never depend on catching a database constraint violation.
/// </summary>
public sealed class FeatureAnnouncementServiceTests
{
    private const string User = "user-1";
    private const string OtherUser = "user-2";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeFeatureAnnouncementRepository _repository = new();
    private readonly StubCurrentActor _actor = new(User);
    private readonly FixedClock _clock = new(Now);
    private readonly FeatureAnnouncementService _service;

    public FeatureAnnouncementServiceTests() =>
        _service = new FeatureAnnouncementService(
            _repository,
            _actor,
            _clock,
            new UnacknowledgedFeaturesQueryValidator(),
            new AcknowledgeFeaturesCommandValidator());

    [Fact]
    public async Task Nothing_is_returned_when_there_are_no_announcements() =>
        Assert.Empty(await GetAsync("/reports"));

    [Fact]
    public async Task A_retired_announcement_is_never_returned()
    {
        _repository.Seed("retired", isActive: false);

        Assert.Empty(await GetAsync("/reports"));
    }

    [Fact]
    public async Task Only_announcements_matching_the_path_are_returned()
    {
        _repository.Seed("everywhere");
        _repository.Seed("reports-only", "[\"/reports\"]");
        _repository.Seed("settings-only", "[\"/settings\"]");

        var announcements = await GetAsync("/reports/monthly");

        Assert.Equal(new[] { "everywhere", "reports-only" }, announcements.Select(a => a.Key).Order().ToArray());
    }

    [Fact]
    public async Task A_traversal_path_is_resolved_before_matching()
    {
        _repository.Seed("reports-only", "[\"/reports\"]");

        // Resolves to /admin, so the reports-scoped announcement must not surface — even though the
        // raw string starts with "/reports".
        Assert.Empty(await GetAsync("/reports/../admin"));
    }

    [Fact]
    public async Task Results_are_ordered_by_display_order_then_creation_time()
    {
        _repository.Seed("third", displayOrder: 2);
        _repository.Seed("second", displayOrder: 1, createdAt: Now.AddHours(1));
        _repository.Seed("first", displayOrder: 1, createdAt: Now);

        var announcements = await GetAsync("/");

        Assert.Equal(new[] { "first", "second", "third" }, announcements.Select(a => a.Key).ToArray());
    }

    [Fact]
    public async Task An_acknowledged_announcement_is_not_returned_again()
    {
        var announcement = _repository.Seed("seen");

        await AcknowledgeAsync(announcement.Id);

        Assert.Empty(await GetAsync("/"));
    }

    [Fact]
    public async Task One_users_acknowledgement_does_not_affect_another()
    {
        var announcement = _repository.Seed("shared");
        await AcknowledgeAsync(announcement.Id);

        _actor.Id = OtherUser;

        Assert.Equal("shared", Assert.Single(await GetAsync("/")).Key);
    }

    [Fact]
    public async Task Acknowledging_twice_writes_nothing_the_second_time()
    {
        var announcement = _repository.Seed("dismissed");

        await AcknowledgeAsync(announcement.Id);
        Assert.Single(_repository.Acknowledgements);
        Assert.Equal(1, _repository.SaveCount);

        // A double-click, or a retried request after a flaky network. Idempotency is computed in the
        // application, so this must not even reach the database — no insert, no save, no
        // unique-constraint violation to catch and translate back into the success it always was.
        await AcknowledgeAsync(announcement.Id);

        Assert.Single(_repository.Acknowledgements);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task A_repeated_id_within_one_request_is_written_once()
    {
        var announcement = _repository.Seed("dismissed");

        await _service.AcknowledgeAsync(
            new AcknowledgeFeaturesCommand([announcement.Id, announcement.Id]), CancellationToken.None);

        Assert.Single(_repository.Acknowledgements);
    }

    [Fact]
    public async Task Acknowledging_records_the_user_the_announcement_and_the_time()
    {
        var announcement = _repository.Seed("dismissed");

        await AcknowledgeAsync(announcement.Id);

        var acknowledgement = Assert.Single(_repository.Acknowledgements);
        Assert.Equal(User, acknowledgement.UserId);
        Assert.Equal(announcement.Id, acknowledgement.FeatureId);
        Assert.Equal(Now, acknowledgement.AcknowledgedAt);
    }

    [Fact]
    public async Task Acknowledging_an_empty_list_is_an_accepted_no_op()
    {
        await _service.AcknowledgeAsync(new AcknowledgeFeaturesCommand([]), CancellationToken.None);

        Assert.Empty(_repository.Acknowledgements);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task An_id_naming_no_announcement_is_dropped_rather_than_inserted()
    {
        // Otherwise the foreign key rejects the whole batch with a 500 — including in the legitimate
        // race where the announcement was deleted between the lookup and the dismissal.
        await AcknowledgeAsync(Guid.NewGuid());

        Assert.Empty(_repository.Acknowledgements);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task An_unknown_id_alongside_a_real_one_does_not_block_it()
    {
        var announcement = _repository.Seed("dismissed");

        await _service.AcknowledgeAsync(
            new AcknowledgeFeaturesCommand([Guid.NewGuid(), announcement.Id]), CancellationToken.None);

        Assert.Equal(announcement.Id, Assert.Single(_repository.Acknowledgements).FeatureId);
    }

    [Theory]
    [InlineData(ActorIdentifiers.Anonymous)]
    [InlineData(ActorIdentifiers.System)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_caller_with_no_real_identity_is_refused(string actorId)
    {
        _repository.Seed("anything");
        _actor.Id = actorId;

        // Per-user state cannot be attributed to a shared pseudo-identity: doing so would mark an
        // announcement dismissed for every caller who is not signed in.
        await Assert.ThrowsAsync<ForbiddenException>(() => GetAsync("/"));
        await Assert.ThrowsAsync<ForbiddenException>(() => AcknowledgeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task An_oversized_path_is_rejected_before_any_lookup() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            GetAsync(new string('a', UnacknowledgedFeaturesQueryValidator.MaxPathLength + 1)));

    [Fact]
    public async Task An_unbounded_acknowledgement_batch_is_rejected()
    {
        var ids = Enumerable.Range(0, AcknowledgeFeaturesCommand.MaxFeatureIds + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.AcknowledgeAsync(new AcknowledgeFeaturesCommand(ids), CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_id_is_rejected() =>
        await Assert.ThrowsAsync<ValidationException>(() => AcknowledgeAsync(Guid.Empty));

    private async Task<IReadOnlyList<FeatureAnnouncementDto>> GetAsync(string? path) =>
        await _service.GetUnacknowledgedAsync(new UnacknowledgedFeaturesQuery(path), CancellationToken.None);

    private Task AcknowledgeAsync(Guid featureId) =>
        _service.AcknowledgeAsync(new AcknowledgeFeaturesCommand([featureId]), CancellationToken.None);
}
