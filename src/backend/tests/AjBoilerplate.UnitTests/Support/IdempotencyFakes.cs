using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Domain.Idempotency;

namespace AjBoilerplate.UnitTests.Support;

/// <summary>
/// An in-memory <see cref="IIdempotencyRepository"/>. Deliberately a hand-written fake rather than a
/// mock: the behaviour under test is stateful and order-dependent — claim, complete, re-read — which
/// per-call mock setups model badly.
///
/// It does NOT simulate the unique-index violation the service catches. Faking that would mean
/// constructing a <c>SqlException</c>, which has no public constructor and would tie these tests to
/// <c>Microsoft.Data.SqlClient</c>'s internals — brittle across a patch bump, and it would only ever
/// prove that the fake throws what the fake was told to throw. That the index really rejects the
/// second concurrent claim, and that the loser then replays the winner's response instead of
/// executing, is proven against a containerised SQL Server in
/// <c>IdempotencyApiTests.Concurrent_requests_sharing_a_key_create_exactly_one_record</c>. Same
/// division of labour as <c>FakeItemRepository</c> and the <c>rowversion</c> it cannot really issue.
/// </summary>
public sealed class FakeIdempotencyRepository : IIdempotencyRepository
{
    private readonly List<IdempotencyRecord> _records = [];
    private readonly List<IdempotencyRecord> _pendingAdds = [];

    public IReadOnlyList<IdempotencyRecord> Records => _records;

    public Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_records.Find(r =>
            string.Equals(r.Scope, scope, StringComparison.Ordinal)
            && string.Equals(r.Key, key, StringComparison.Ordinal)));

    public Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        _pendingAdds.Add(record);
        return Task.CompletedTask;
    }

    public void Remove(IdempotencyRecord record)
    {
        _records.Remove(record);
        _pendingAdds.Remove(record);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        _records.AddRange(_pendingAdds);
        _pendingAdds.Clear();
        return Task.CompletedTask;
    }

    public void ClearChangeTracking() => _pendingAdds.Clear();
}
