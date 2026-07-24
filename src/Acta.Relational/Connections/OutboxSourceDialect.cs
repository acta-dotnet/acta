using System.Data.Common;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Relational.Commands;

namespace Acta.Relational.Connections;

/// <summary>
/// Base <see cref="ISqlDialect"/> for the external-outbox source session. Unlike the ledger dialects,
/// every provider drives its outbox claim/finalize commands through inline SQL (Acta installs no routine
/// in a producer database), so <see cref="SupportsRoutines"/> is always false, the claim's RETURNING /
/// OUTPUT rows come from the last statement, and the two-statement claim runs under one write
/// transaction. The ledger-only binder surface (enqueue / definition / completion) is never reached from
/// the outbox store and throws. Providers supply connection creation, parameter binding, and transient
/// classification.
/// </summary>
internal abstract class OutboxSourceDialect : ISqlDialect
{
    public abstract DbProvider Provider { get; }

    public abstract string DialectToken { get; }

    public bool SupportsRoutines => false;

    public bool ResultSetIsLast => true;

    public bool WrapsMutationInTransaction => true;

    // Recover-expired then claim run as two statements; one transaction gives the claim statement the
    // recovered rows and keeps the lease stamp atomic. Providers may override for a stricter begin mode.
    public virtual DbTransaction BeginImmediateTransaction(DbConnection connection) => connection.BeginTransaction();

    public virtual bool IsTransientConflict(Exception exception) => false;

    public virtual bool IsCancellation(Exception exception) => false;

    public abstract DbConnection CreateConnection(string connectionString);

    public abstract bool OwnsConnection(DbConnection connection);

    public abstract DbParameter CreateParameter(DbParameterSpec spec);

    // The outbox source never invokes a routine or a ledger bulk binder; the store composes inline SQL
    // and binds scalar parameters only. These stay unreachable rather than shipping a second binder path.
    private static NotSupportedException NotOutbox([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new($"The external-outbox source dialect runs inline SQL only; '{member}' is not part of its surface.");

    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName) => throw NotOutbox();

    public void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema) => throw NotOutbox();

    public void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema) =>
        throw NotOutbox();

    public void BindRegisterJobDefinitions(
        DbCommand command,
        short namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<JobDefinitionRow> rows,
        string schema
    ) => throw NotOutbox();

    public void BindRegisterScheduledJobs(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    ) => throw NotOutbox();

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema) => throw NotOutbox();

    public void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema) =>
        throw NotOutbox();
}
