using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Acta.Relational.Resources;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Sqlite.Configuration;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// Unit tests for the boundary of <c>DbSession</c>'s transient-retry region. The retry covers open,
/// begin, bind, and execute; commit and teardown sit outside it. A transient raised once the batch has
/// landed must therefore surface rather than replay the batch against rows it already changed - the
/// shape that turned a committed <c>complete_step</c> into a spurious lost CAS. Driven through fake
/// ADO.NET objects because no real provider lets a commit be made to fail on demand.
/// </summary>
public class DbSessionRetryRegionTests
{
    private sealed class TransientException : Exception { }

    private static readonly StoreCommand Command = new("Execution", "CompleteStep");

    private static DbSession NewSession(FakeDialect dialect, int retryAttempts = 5) =>
        new(
            new SqliteProviderOptions
            {
                ConnectionString = "Data Source=:memory:",
                Schema = "acta",
                DeadlockRetryAttempts = retryAttempts,
            },
            dialect,
            new SqlResourceCatalog(typeof(DbSessionRetryRegionTests).Assembly, "acta")
        );

    [Fact]
    public async Task Commit_and_teardown_run_once_after_the_batch_executes()
    {
        var log = new List<string>();
        var dialect = new FakeDialect(log);
        var ct = TestContext.Current.CancellationToken;

        await NewSession(dialect).ExecuteAsync(Command, static _ => { }, ct);

        Assert.Equal(new[] { "open", "begin", "execute", "commit", "tx-dispose", "conn-dispose" }, log);
    }

    [Fact]
    public async Task Transient_during_execution_replays_the_batch_on_a_fresh_connection()
    {
        var log = new List<string>();
        var dialect = new FakeDialect(log) { FailExecuteWhile = attempt => attempt == 1 };
        var ct = TestContext.Current.CancellationToken;

        await NewSession(dialect).ExecuteAsync(Command, static _ => { }, ct);

        // The failed attempt is torn down transaction-first before the retry opens again: a retry that
        // began while the previous transaction was still alive would deadlock against its own write lock.
        Assert.Equal(
            new[] { "open", "begin", "tx-dispose", "conn-dispose", "open", "begin", "execute", "commit", "tx-dispose", "conn-dispose" },
            log
        );
        Assert.Equal(2, dialect.Executions);
        Assert.Equal(1, dialect.Commits);
    }

    [Fact]
    public async Task Transient_during_commit_surfaces_without_replaying_the_batch()
    {
        var log = new List<string>();
        var dialect = new FakeDialect(log) { FailCommit = true };
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TransientException>(() => NewSession(dialect).ExecuteAsync(Command, static _ => { }, ct));

        // One execution, and the connection is still released: the caller's own budget owns recovery,
        // and it must not be handed a leaked connection along with the failure.
        Assert.Equal(1, dialect.Executions);
        Assert.Equal(new[] { "open", "begin", "execute", "commit-failed", "tx-dispose", "conn-dispose" }, log);
    }

    [Fact]
    public async Task Transient_during_execution_still_exhausts_the_attempt_budget()
    {
        var log = new List<string>();
        var dialect = new FakeDialect(log) { FailExecuteWhile = static _ => true };
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TransientException>(() =>
            NewSession(dialect, retryAttempts: 3).ExecuteAsync(Command, static _ => { }, ct)
        );

        Assert.Equal(3, dialect.Executions);
        Assert.Equal(0, dialect.Commits);
        Assert.Equal(3, log.Count(entry => entry == "conn-dispose"));
    }

    [Fact]
    public async Task Teardown_that_fails_still_releases_the_connection_and_keeps_the_original_transient()
    {
        var log = new List<string>();
        // The first attempt fails mid-execute AND its rollback then fails too - a transaction disposed
        // on a connection the database has already aborted is the likeliest thing in teardown to throw.
        var dialect = new FakeDialect(log)
        {
            FailExecuteWhile = static attempt => attempt == 1,
            FailTransactionDisposeWhile = static ordinal => ordinal == 1,
        };
        var ct = TestContext.Current.CancellationToken;

        await NewSession(dialect).ExecuteAsync(Command, static _ => { }, ct);

        // The connection is released despite the throwing rollback, and the retry still happens: it is
        // the original transient that reaches DeadlockRetry's filter, not the dispose failure, which is
        // not transient and would have abandoned the attempt.
        Assert.Equal(
            new[]
            {
                "open",
                "begin",
                "tx-dispose-failed",
                "conn-dispose",
                "open",
                "begin",
                "execute",
                "commit",
                "tx-dispose",
                "conn-dispose",
            },
            log
        );
        Assert.Equal(2, dialect.Executions);
        Assert.Equal(1, dialect.Commits);
    }

    [Fact]
    public async Task Teardown_that_fails_does_not_replace_the_exception_the_caller_sees()
    {
        var log = new List<string>();
        var dialect = new FakeDialect(log) { FailExecuteWhile = static _ => true, FailTransactionDisposeWhile = static _ => true };
        var ct = TestContext.Current.CancellationToken;

        // Once the budget is spent it is still the statement's failure that surfaces; a caller reading
        // the exception to decide what to do must not be handed a rollback's complaint instead.
        await Assert.ThrowsAsync<TransientException>(() =>
            NewSession(dialect, retryAttempts: 2).ExecuteAsync(Command, static _ => { }, ct)
        );

        Assert.Equal(2, dialect.Executions);
        Assert.Equal(2, log.Count(entry => entry == "conn-dispose"));
    }

    // Fake provider seam: routine-shaped (so no SQL resource is loaded) and transaction-wrapped (so the
    // commit the region boundary is about actually happens). Only the non-generic execute path is
    // driven, so no reader is ever created.
    private sealed class FakeDialect(List<string> log) : ISqlDialect
    {
        public Func<int, bool> FailExecuteWhile { get; init; } = static _ => false;

        public Func<int, bool> FailTransactionDisposeWhile { get; init; } = static _ => false;

        public bool FailCommit { get; init; }

        public int Executions { get; private set; }

        public int Transactions { get; private set; }

        public int Commits { get; private set; }

        public DbProvider Provider => DbProvider.Sqlite;

        public string DialectToken => "sqlite";

        public bool SupportsRoutines => true;

        public bool WrapsMutationInTransaction => true;

        public bool IsTransientConflict(Exception exception) => exception is TransientException;

        public DbConnection CreateConnection(string connectionString) => new FakeConnection(this, log);

        public bool OwnsConnection(DbConnection connection) => connection is FakeConnection;

        public DbTransaction BeginImmediateTransaction(DbConnection connection)
        {
            log.Add("begin");
            Transactions++;
            return new FakeTransaction(this, (FakeConnection)connection, log, Transactions);
        }

        public bool ShouldFailDispose(int transactionOrdinal) => FailTransactionDisposeWhile(transactionOrdinal);

        public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName) => command.CommandText = routineName;

        public bool ShouldFailExecute()
        {
            Executions++;
            return FailExecuteWhile(Executions);
        }

        public bool ShouldFailCommit()
        {
            Commits++;
            return FailCommit;
        }

        public DbParameter CreateParameter(DbParameterSpec spec) => throw new NotSupportedException();

        public void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema) => throw new NotSupportedException();

        public void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema) =>
            throw new NotSupportedException();

        public void BindRegisterJobDefinitions(
            DbCommand command,
            int namespaceId,
            DateTime manifestGenerationUtc,
            IReadOnlyList<JobDefinitionRow> rows,
            string schema
        ) => throw new NotSupportedException();

        public void BindRegisterScheduledJobs(
            DbCommand command,
            IReadOnlyList<DefinitionSchedules> definitions,
            IReadOnlyList<Guid> slotRefs,
            string schema
        ) => throw new NotSupportedException();

        public void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema) =>
            throw new NotSupportedException();

        public void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnection(FakeDialect dialect, List<string> log) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "acta";

        public override string DataSource => "fake";

        public override string ServerVersion => "0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open()
        {
            log.Add("open");
            _state = ConnectionState.Open;
        }

        protected override DbCommand CreateDbCommand() => new FakeCommand(dialect, log) { DbConnectionAccessor = this };

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                log.Add("conn-dispose");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeTransaction(FakeDialect dialect, FakeConnection connection, List<string> log, int ordinal) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;

        protected override System.Data.Common.DbConnection? DbConnection => connection;

        public override void Commit()
        {
            if (dialect.ShouldFailCommit())
            {
                log.Add("commit-failed");
                throw new TransientException();
            }

            log.Add("commit");
        }

        public override void Rollback() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing && dialect.ShouldFailDispose(ordinal))
            {
                log.Add("tx-dispose-failed");
                throw new InvalidOperationException("The rollback could not be performed on an aborted connection.");
            }

            if (disposing)
            {
                log.Add("tx-dispose");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeCommand(FakeDialect dialect, List<string> log) : DbCommand
    {
        public DbConnection? DbConnectionAccessor { get; init; }

        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override System.Data.Common.DbConnection? DbConnection
        {
            get => DbConnectionAccessor;
            set { }
        }

        protected override System.Data.Common.DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();

        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery()
        {
            if (dialect.ShouldFailExecute())
            {
                throw new TransientException();
            }

            log.Add("execute");
            return 1;
        }

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    // Never populated: the test's bind action adds nothing, so every member is unreachable ceremony the
    // DbCommand contract still requires.
    private sealed class FakeParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot { get; } = new();

        public override int Add(object value) => throw new NotSupportedException();

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() { }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) { }

        public override IEnumerator GetEnumerator() => Array.Empty<DbParameter>().GetEnumerator();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => throw new NotSupportedException();

        public override void Remove(object value) => throw new NotSupportedException();

        public override void RemoveAt(int index) => throw new NotSupportedException();

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();

        protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
    }
}
