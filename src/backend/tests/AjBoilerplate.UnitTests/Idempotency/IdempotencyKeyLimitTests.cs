using AjBoilerplate.Api.Infrastructure;
using AjBoilerplate.Domain.Idempotency;

namespace AjBoilerplate.UnitTests.Idempotency;

/// <summary>
/// Pins the one constant this codebase is forced to state twice.
///
/// <c>AjBoilerplate.Api</c> must not reference <c>AjBoilerplate.Domain</c>
/// (<c>DependencyRuleTests.Api_does_not_reference_domain_directly</c>), so
/// <see cref="IdempotencyMiddleware"/> cannot read <see cref="IdempotencyRecord.MaxKeyLength"/> and
/// restates it instead. Only this test project sees both assemblies, so this is the only place the
/// two can be compared — and without the comparison the duplication is a silent trap: raise the
/// Domain's limit alone and the middleware keeps rejecting valid keys; raise the middleware's alone
/// and an over-long key sails past validation to be truncated or rejected by SQL Server, surfacing as
/// a 500 for a request the API should have refused cleanly with a 400.
/// </summary>
public sealed class IdempotencyKeyLimitTests
{
    [Fact]
    public void The_middleware_and_the_column_agree_on_the_longest_key() =>
        Assert.Equal(IdempotencyRecord.MaxKeyLength, IdempotencyMiddleware.MaxKeyLength);
}
