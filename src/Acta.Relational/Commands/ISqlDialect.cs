using System.Data.Common;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;

namespace Acta.Relational.Commands;

/// <summary>
/// Single provider seam: connection creation, parameter binding, routine invocation, and
/// bulk-shape binding for the SQL store. One implementation per <see cref="DbProvider"/>.
/// </summary>
internal interface ISqlDialect
{
    DbProvider Provider { get; }

    /// <summary>Filename dialect token for SQL resource resolution (e.g. <c>mssql</c>, <c>pg</c>).</summary>
    string DialectToken { get; }

    /// <summary>
    /// Whether execution completion accepts a native batch. Independent of SQL resource placement.
    /// </summary>
    bool SupportsBatchCompletion { get; }

    /// <summary>
    /// Whether an inline command's result rows come from the LAST result set. SQLite and external
    /// outbox commands use this convention; server ledger commands expose one business result set.
    /// </summary>
    bool ResultSetIsLast => false;

    /// <summary>
    /// Begins a session-owned write transaction when the provider requires one. Server commands
    /// establish atomicity in the database and return null. SQLite uses BEGIN IMMEDIATE.
    /// </summary>
    DbTransaction? BeginOwnedWriteTransaction(DbConnection connection) => null;

    /// <summary>
    /// Whether an exception is a transient lock conflict (the database aborted this command as a
    /// deadlock victim) that re-running the rolled-back command can recover from. The default treats
    /// nothing as transient; providers that surface deadlock codes (SQL Server 1205, PostgreSQL 40P01)
    /// override.
    /// </summary>
    bool IsTransientConflict(Exception exception) => false;

    /// <summary>
    /// Whether an exception is the provider's way of reporting that the command was aborted by the
    /// caller's own cancellation (SqlClient throws <c>SqlException</c> rather than
    /// <c>OperationCanceledException</c>). Consulted only when the token is already cancelled; the
    /// default treats nothing as cancellation-shaped. Providers whose ADO.NET client honors the
    /// token with a real <c>OperationCanceledException</c> need no override.
    /// </summary>
    bool IsCancellation(Exception exception) => false;

    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Whether <paramref name="connection"/> is this provider's concrete ADO.NET connection type. Used
    /// to reject a caller-owned transaction from a different provider before any command executes. This
    /// is a structural check, not a database-identity probe.
    /// </summary>
    bool OwnsConnection(DbConnection connection);

    /// <summary>
    /// Prepares a caller-owned connection for a transactional enqueue: installs any connection-local SQL
    /// functions the provider's enqueue body requires and verifies its non-negotiable invariants. Routine
    /// providers need nothing (their routines join the caller's transaction as-is), so the default is a
    /// no-op. SQLite installs its blob/error functions and verifies <c>foreign_keys</c> is enabled without
    /// altering the caller's busy timeout, synchronous mode, or transaction kind.
    /// </summary>
    void PrepareCallerConnection(DbConnection connection) { }

    DbParameter CreateParameter(DbParameterSpec spec);

    void ConfigureRoutineCommand(DbCommand command, string schema, string routineName);

    /// <summary>Binds parameters required by a provider's non-recurring completion implementation.</summary>
    void BindNonRecurringCompletionDefaults(DbCommand command, CompleteExecutionRequest request) { }

    /// <summary>Binds a one-row enqueue in the provider-native shape (typed arrays / TVP / JSON).</summary>
    void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema);

    /// <summary>Binds a whole-batch enqueue in the provider-native shape; jobRefs align with rows.</summary>
    void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema);

    /// <summary>Binds the whole-namespace definition registration batch in the provider-native shape.</summary>
    void BindRegisterJobDefinitions(
        DbCommand command,
        int namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<JobDefinitionRow> rows,
        string schema
    );

    /// <summary>Binds the scheduled-job slot + schedule registration batches in the provider-native shape.</summary>
    void BindRegisterScheduledJobs(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    );

    /// <summary>Binds a recurring-completion command (scalars plus the schedule-advance batch) in the provider-native shape.</summary>
    void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema);

    /// <summary>Binds the whole-batch execution-completion set in the provider-native shape (routine providers only).</summary>
    void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema);
}
