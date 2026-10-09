using System.Data.Common;

namespace Acta;

/// <summary>
/// Base <see cref="ISqlDialect"/> for the external-outbox source session. Unlike the ledger dialects, every provider
/// drives its outbox claim and finalize commands through inline SQL, since Acta installs no routine in a producer
/// database; the claim's RETURNING or OUTPUT rows come from the last statement, and the two-statement claim runs
/// under one write transaction. The ledger-only binder surface (enqueue, definition, completion) is never reached
/// from the outbox store and throws. Providers supply connection creation, parameter binding, and transient classification.
/// </summary>
internal abstract class OutboxSourceDialect : ISqlDialect
{
    public abstract DbProvider Provider { get; }

    public abstract string DialectToken { get; }

    public bool ResultSetIsLast => true;

    /// <summary>
    /// Recover-expired then claim run as two statements; one transaction gives the claim statement the
    /// recovered rows and keeps the lease stamp atomic. Servers own it inside the command; SQLite
    /// overrides this hook to begin an immediate transaction.
    /// </summary>
    public virtual DbTransaction? BeginOwnedWriteTransaction(DbConnection connection) => null;

    public virtual bool IsTransientConflict(Exception exception) => false;

    public virtual bool IsCancellation(Exception exception) => false;

    public abstract DbConnection CreateConnection(string connectionString);

    public abstract bool OwnsConnection(DbConnection connection);

    public abstract DbParameter CreateParameter(DbParameterSpec spec);

    /// <summary>
    /// The outbox source never invokes a routine or a ledger bulk binder; the store composes inline SQL
    /// and binds scalar parameters only. These stay unreachable rather than shipping a second binder path.
    /// </summary>
    private static NotSupportedException NotOutbox([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new($"The external-outbox source dialect runs inline SQL only; '{member}' is not part of its surface.");

    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName) => throw NotOutbox();

    public void BindExcludedDefinitionIds(DbCommand command, int[]? definitionIds) => throw NotOutbox();

    public void BindHeldLeases(
        DbCommand command,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        IReadOnlyList<LockToken> locks
    ) => throw NotOutbox();

    public void BindEnqueueJob(DbCommand command, JobEnqueueValues row, Guid jobRef, string schema) => throw NotOutbox();

    public void BindEnqueueJobs(DbCommand command, IReadOnlyList<JobEnqueueValues> rows, IReadOnlyList<Guid> jobRefs, string schema) =>
        throw NotOutbox();

    public void BindRegisterDefinitions(
        DbCommand command,
        int namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<DefinitionValues> rows,
        string schema
    ) => throw NotOutbox();

    public void BindRegisterSchedules(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    ) => throw NotOutbox();

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionCommand request, string schema) => throw NotOutbox();
}
