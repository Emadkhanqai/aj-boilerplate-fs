using System.Text;
using AjBoilerplate.Application.Idempotency;
using AjBoilerplate.Domain.Idempotency;
using AjBoilerplate.UnitTests.Support;

namespace AjBoilerplate.UnitTests.Idempotency;

/// <summary>
/// The claim/replay decision table behind <c>Idempotency-Key</c>.
///
/// These cover the decisions the service can make from state it can see. The one it cannot — two
/// concurrent claims of the same key racing past the same "not claimed yet" lookup, where only the
/// unique index stops the second — is proven against a real SQL Server in <c>IdempotencyApiTests</c>,
/// for the reason set out on <see cref="FakeIdempotencyRepository"/>.
/// </summary>
public sealed class IdempotencyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Scope = "user-1";
    private const string Key = "key-1";
    private const string Hash = "HASH-A";
    private const string OtherHash = "HASH-B";
    private const string Location = "/api/v1/items/1";

    [Fact]
    public async Task An_unclaimed_key_is_claimed_and_the_caller_proceeds()
    {
        var (service, repository) = NewService();

        var claim = await BeginAsync(service);

        Assert.Equal(IdempotencyDecision.Proceed, claim.Decision);
        Assert.Single(repository.Records);
        Assert.Equal(IdempotencyRecordStatus.InProgress, repository.Records[0].Status);
    }

    [Fact]
    public async Task A_completed_key_replays_the_stored_response_verbatim()
    {
        var (service, _) = NewService();
        await BeginAsync(service);
        await service.CompleteAsync(Scope, Key, 201, "application/json", Location, Body("{\"id\":1}"), CancellationToken.None);

        var claim = await BeginAsync(service);

        Assert.Equal(IdempotencyDecision.Replay, claim.Decision);
        Assert.Equal(201, claim.StatusCode);
        Assert.Equal("application/json", claim.ContentType);
        // A 201's Location is part of the response, not decoration — a client follows it to read back
        // what it created, so a replay that dropped it would not be replaying the same response.
        Assert.Equal(Location, claim.Location);
        Assert.Equal("{\"id\":1}", Encoding.UTF8.GetString(claim.Body));
    }

    [Fact]
    public async Task A_key_whose_first_request_is_still_running_is_refused()
    {
        var (service, _) = NewService();
        await BeginAsync(service);

        // There is no response to return yet, and executing would be the double-write the key exists
        // to prevent. Refusing and inviting a retry is the only correct answer.
        Assert.Equal(IdempotencyDecision.InProgress, (await BeginAsync(service)).Decision);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_refused_rather_than_replayed()
    {
        var (service, _) = NewService();
        await BeginAsync(service);
        await service.CompleteAsync(Scope, Key, 201, "application/json", Location, Body("{}"), CancellationToken.None);

        // Replaying here would silently discard the second request; executing it would break the
        // guarantee the key asked for. Neither is acceptable, so the client is told it has a bug.
        Assert.Equal(
            IdempotencyDecision.RequestMismatch,
            (await BeginAsync(service, hash: OtherHash)).Decision);
    }

    [Fact]
    public async Task A_mismatched_request_is_refused_even_while_the_original_is_still_running()
    {
        var (service, _) = NewService();
        await BeginAsync(service);

        // Checked before the status, so the client gets the actionable answer rather than being sent
        // into a retry loop for a request that will never be accepted under this key.
        Assert.Equal(
            IdempotencyDecision.RequestMismatch,
            (await BeginAsync(service, hash: OtherHash)).Decision);
    }

    [Fact]
    public async Task Two_callers_never_share_a_key()
    {
        var (service, repository) = NewService();
        await BeginAsync(service);

        // Same key, different person. A shared record would leak one caller's response body to the
        // other — this is a privacy boundary, not a partitioning convenience.
        var other = await service.BeginAsync("user-2", Key, "POST", "/api/v1/items", Hash, CancellationToken.None);

        Assert.Equal(IdempotencyDecision.Proceed, other.Decision);
        Assert.Equal(2, repository.Records.Count);
    }

    [Fact]
    public async Task Abandoning_an_unfinished_claim_frees_the_key_for_a_genuine_retry()
    {
        var (service, repository) = NewService();
        await BeginAsync(service);

        await service.AbandonAsync(Scope, Key, CancellationToken.None);

        Assert.Empty(repository.Records);
        // A transient failure must not burn the key the client will retry with.
        Assert.Equal(IdempotencyDecision.Proceed, (await BeginAsync(service)).Decision);
    }

    [Fact]
    public async Task Abandoning_never_drops_a_stored_response()
    {
        var (service, repository) = NewService();
        await BeginAsync(service);
        await service.CompleteAsync(Scope, Key, 201, "application/json", Location, Body("{}"), CancellationToken.None);

        await service.AbandonAsync(Scope, Key, CancellationToken.None);

        // Dropping it would turn a successful create into one that can be replayed into a SECOND
        // create — the precise failure the mechanism exists to prevent.
        Assert.Single(repository.Records);
        Assert.Equal(IdempotencyDecision.Replay, (await BeginAsync(service)).Decision);
    }

    [Fact]
    public async Task Completing_a_key_that_is_already_completed_is_a_no_op()
    {
        var (service, _) = NewService();
        await BeginAsync(service);
        await service.CompleteAsync(Scope, Key, 201, "application/json", Location, Body("first"), CancellationToken.None);

        // Must not throw: the caller's response is already on its way out, and there is nothing worth
        // failing the request over.
        await service.CompleteAsync(Scope, Key, 500, "application/json", Location, Body("second"), CancellationToken.None);

        Assert.Equal("first", Encoding.UTF8.GetString((await BeginAsync(service)).Body));
    }

    [Fact]
    public async Task Completing_or_abandoning_an_unknown_key_is_a_no_op()
    {
        var (service, _) = NewService();

        await service.CompleteAsync(Scope, "never-claimed", 201, null, null, [], CancellationToken.None);
        await service.AbandonAsync(Scope, "never-claimed", CancellationToken.None);
    }

    private static Task<IdempotencyClaim> BeginAsync(IIdempotencyService service, string hash = Hash) =>
        service.BeginAsync(Scope, Key, "POST", "/api/v1/items", hash, CancellationToken.None);

    private static byte[] Body(string content) => Encoding.UTF8.GetBytes(content);

    private static (IIdempotencyService Service, FakeIdempotencyRepository Repository) NewService()
    {
        var repository = new FakeIdempotencyRepository();
        return (new IdempotencyService(repository, new FixedClock(Now)), repository);
    }
}
