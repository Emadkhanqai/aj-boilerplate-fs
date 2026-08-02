using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Application.Persistence;

/// <summary>
/// Detects whether a caught <see cref="DbUpdateException"/> was caused specifically by a SQL Server
/// unique-constraint (or unique-index) violation — error 2601 ("Cannot insert duplicate key row...")
/// or 2627 ("Violation of ... constraint... Cannot insert duplicate key...").
///
/// This is the shape a check-then-act race takes under true concurrency, and it is NOT what EF
/// Core's own <see cref="DbUpdateConcurrencyException"/> (a RowVersion mismatch) covers, because no
/// RowVersion check is even reached before the unique index rejects the INSERT. The canonical case
/// in this codebase is inbox dedup: genuine concurrent replay of the same source event can pass the
/// "not yet processed" lookup more than once before either commits, tripping the unique index on
/// <c>InboxMessages.SourceEventId</c> — which should become an idempotent success, not a 500.
///
/// Deliberately narrow: only these two SQL Server error numbers match. Every other
/// <see cref="DbUpdateException"/> (a genuinely unexpected database error) is left for the caller to
/// rethrow unchanged, so this never masks a real failure as a false idempotent/409 result.
/// </summary>
public static class SqlUniqueConstraintViolation
{
    private const int CannotInsertDuplicateKeyRow = 2601;
    private const int UniqueOrPrimaryKeyConstraintViolation = 2627;

    public static bool Matches(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException
        && sqlException.Number is CannotInsertDuplicateKeyRow or UniqueOrPrimaryKeyConstraintViolation;
}
