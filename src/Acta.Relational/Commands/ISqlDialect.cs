using System.Data.Common;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;

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
    /// Whether this provider installs store commands as stored routines (procedures/functions). SQL Server
    /// and PostgreSQL return <c>true</c>; an inline-only provider returns <c>false</c>, routing every
    /// command through its inline <c>.sql</c> body.
    /// </summary>
    bool SupportsRoutines { get; }

    /// <summary>
    /// Whether a store command's result rows come from the LAST statement of a multi-statement body
    /// rather than the first. Routine providers (one stored routine, one result set) return
    /// <c>false</c>; an inline-only provider (SQLite) runs the command as a sequence of statements
    /// whose final statement is the result SELECT (leading statements may be validation guards or
    /// writes), so the reader advances to the last result set.
    /// </summary>
    bool ResultSetIsLast => false;

    /// <summary>
    /// Whether the store wraps execute-style calls in its own write transaction. Routine providers
    /// return <c>false</c>: a stored routine is already atomic. An inline-only provider (SQLite)
    /// returns <c>true</c> so write bodies run all-or-nothing under one <c>BEGIN IMMEDIATE</c> write
    /// lock, while read-style calls stay lock-free.
    /// </summary>
    bool WrapsMutationInTransaction => false;

    /// <summary>
    /// Begins the write transaction used for execute-style calls when
    /// <see cref="WrapsMutationInTransaction"/> is set. SQLite takes the reserved write lock up front
    /// (<c>BEGIN IMMEDIATE</c>) so a busy writer waits rather than failing late on lock upgrade.
    /// </summary>
    DbTransaction BeginImmediateTransaction(DbConnection connection) =>
        throw new NotSupportedException("This dialect does not wrap store commands in a write transaction.");

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

    /// <summary>Binds a one-row enqueue in the provider-native shape (typed arrays / TVP / JSON).</summary>
    void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema);

    /// <summary>Binds a whole-batch enqueue in the provider-native shape; jobRefs align with rows.</summary>
    void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema);

    /// <summary>Binds the whole-namespace definition registration batch in the provider-native shape.</summary>
    void BindRegisterJobDefinitions(
        DbCommand command,
        short namespaceId,
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
