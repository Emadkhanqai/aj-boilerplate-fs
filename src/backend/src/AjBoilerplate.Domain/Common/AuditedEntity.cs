namespace AjBoilerplate.Domain.Common;

/// <summary>
/// Base class for an aggregate root that carries creation/modification timestamps and an optimistic
/// concurrency token. Every persisted root in this codebase inherits it, so the EF Core
/// configurations map the three shared columns once (<c>AuditedEntityConfiguration.Apply</c>)
/// instead of restating them per entity.
///
/// <see cref="RowVersion"/> is a SQL Server <c>rowversion</c>: the DATABASE issues and advances it
/// on every insert and update, and EF Core puts the loaded value into the <c>WHERE</c> clause of
/// every UPDATE and DELETE. That placement is the whole point — a lost update is rejected by the
/// engine, inside the same statement, with no window between the check and the write. A token the
/// application assigns cannot make that guarantee, because the value it compares was read in an
/// earlier statement.
///
/// It is empty on a newly constructed entity and is populated by the database on first save, so
/// nothing should read it before then.
/// </summary>
public abstract class AuditedEntity
{
    /// <summary>When this entity was first persisted, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; protected set; }

    /// <summary>When this entity was last modified, in UTC; null while it has never been modified.</summary>
    public DateTimeOffset? UpdatedAt { get; protected set; }

    /// <summary>
    /// The database-issued optimistic concurrency token. Read it, hand it to the client, and require
    /// it back on the next write; never construct one.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Stamps <see cref="UpdatedAt"/>. Every mutating domain method must call this as its last step.
    /// <see cref="RowVersion"/> is deliberately NOT touched here: the database advances it on the
    /// resulting UPDATE, and assigning it in memory would overwrite the loaded original value that
    /// EF Core needs for the concurrency predicate — silently disabling the check.
    /// </summary>
    protected void Touch(DateTimeOffset nowUtc) => UpdatedAt = nowUtc;

    /// <summary>Stamps <see cref="CreatedAt"/> during construction of a brand-new entity.</summary>
    protected void MarkCreated(DateTimeOffset nowUtc) => CreatedAt = nowUtc;
}
