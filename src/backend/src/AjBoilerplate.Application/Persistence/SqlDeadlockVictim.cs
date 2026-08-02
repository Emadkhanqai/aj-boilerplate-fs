using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Application.Persistence;

/// <summary>
/// Detects whether a caught exception was caused specifically by SQL Server choosing the current
/// transaction as a deadlock victim — error 1205 ("Transaction (Process ID N) was deadlocked on ...
/// resources with another process and has been chosen as the deadlock victim. Rerun the
/// transaction."). Broader than <see cref="SqlUniqueConstraintViolation"/>'s own narrow shape: a
/// deadlock can surface either as a raw <see cref="SqlException"/> from a plain query (which EF Core
/// does not wrap) or as a <see cref="DbUpdateException"/> wrapping one (a deadlock hit inside
/// <c>SaveChangesAsync</c>), so this checks both the exception itself and its
/// <see cref="Exception.InnerException"/>.
///
/// SQL Server's own guidance for error 1205 is simply "rerun the transaction" — the victim's
/// transaction is already fully rolled back by the time this error is raised, so no partial state
/// survives to worry about; a bounded, immediate retry (no fixed sleep — the winning transaction's
/// locks are already releasing as its own commit/rollback completes) of the whole logical operation
/// is the correct, standard response, not a raw 500.
/// </summary>
public static class SqlDeadlockVictim
{
    private const int DeadlockVictim = 1205;

    public static bool Matches(Exception exception) =>
        (exception is SqlException direct && direct.Number == DeadlockVictim)
        || (exception.InnerException is SqlException inner && inner.Number == DeadlockVictim);
}
