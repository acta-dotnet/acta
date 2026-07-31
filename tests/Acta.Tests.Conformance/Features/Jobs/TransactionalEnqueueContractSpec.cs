using System.Data;
using System.Data.Common;
using System.Globalization;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// The full direct caller-transaction enqueue contract, beyond the commit/rollback smoke: typed and
/// explicit-contract overloads reach the transactional store path, deduplicated outcomes are provisional,
/// structurally invalid transactions (null, detached/disposed, wrong provider, closed connection) are
/// rejected before any command, an enqueue rejection requires full caller rollback with no Acta retry,
/// no transactional enqueue publishes a worker wakeup, and Acta neither commits nor rolls back the
/// caller's transaction. Provider-specific preparation lives in the per-provider spec heads.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.transactional-contract",
    "Transactional enqueue is provisional, validated, wake-free, and caller-owned",
    Area = "Enqueue",
    Contract = "Every transactional enqueue overload joins the caller transaction, rejects invalid transactions, publishes no wakeup, and leaves completion to the caller.",
    Arrange = "The test namespace is registered and a one-column business probe table exists in the Acta schema.",
    Act = "Typed, contract, deduplicated, rejected, and invalid transactional enqueues run against a caller transaction with a recording wakeup seam installed.",
    Assert = "Each overload persists or vanishes with the caller transaction, invalid transactions throw before executing, and no wakeup is published."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneInTransactionAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchInTransactionAsync))]
public abstract class TransactionalEnqueueContractSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string ProbeTable = "acta_txn_contract_probe";

    private readonly RecordingWakeup _wakeup = new();

    // Enqueue-only: RunAsWorker keeps InitializeAsync so the per-test namespace is registered (enqueue
    // resolves it), but the claim/execute loop is never started - identical to EnqueueSpec.
    protected override bool RunAsWorker => true;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register the recording wakeup before UseActa's TryAddSingleton so the no-wake fact can observe
        // that a transactional enqueue publishes nothing while an owned enqueue publishes one.
        services.AddSingleton<IWorkerWakeup>(_wakeup);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "Typed and explicit-contract transactional overloads persist and vanish with the caller transaction")]
    public async Task Typed_and_contract_overloads_reach_the_transactional_store_path()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await Db.OpenConnectionAsync(ct);

        long typedCommitted;
        long contractCommitted;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            typedCommitted = (await Jobs.EnqueueAsync(tx, new AddNumbers(2, 3), ct: ct)).JobId;
            contractCommitted = (await Jobs.EnqueueAsync(tx, TestJobsManifest.AddNumbers, new AddNumbers(4, 5), ct: ct)).JobId;
            await tx.CommitAsync(ct);
        }

        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == typedCommitted).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == contractCommitted).SingleOrDefaultAsync(ct));

        long typedRolledBack;
        long contractRolledBack;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            typedRolledBack = (await Jobs.EnqueueAsync(tx, new AddNumbers(6, 7), ct: ct)).JobId;
            contractRolledBack = (await Jobs.EnqueueAsync(tx, TestJobsManifest.AddNumbers, new AddNumbers(8, 9), ct: ct)).JobId;
            await tx.RollbackAsync(ct);
        }

        Assert.Null(await Db.From<Job>().Where(j => j.Id == typedRolledBack).SingleOrDefaultAsync(ct));
        Assert.Null(await Db.From<Job>().Where(j => j.Id == contractRolledBack).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A deduplicated transactional outcome is provisional so rollback leaves no durable row")]
    public async Task Deduplicated_outcomes_are_provisional()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("txn-dedup");

        await using var conn = await Db.OpenConnectionAsync(ct);

        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var inserted = await Jobs.EnqueueAsync(tx, Request(key: key), ct);
            Assert.Equal(JobEnqueueAction.Inserted, inserted.Action);

            // The second enqueue sees the first row uncommitted on the same connection, so it dedupes.
            var deduplicated = await Jobs.EnqueueAsync(tx, Request(key: key), ct);
            Assert.Equal(JobEnqueueAction.Deduplicated, deduplicated.Action);
            Assert.Equal(inserted.JobId, deduplicated.JobId);

            await tx.RollbackAsync(ct);
        }

        // The provisional identity never became durable.
        Assert.Null(await Db.From<Job>().Where(j => j.DeduplicationKey == key).SingleOrDefaultAsync(ct));

        // A re-enqueue after rollback inserts a fresh row - the earlier dedupe left nothing behind.
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var reinserted = await Jobs.EnqueueAsync(tx, Request(key: key), ct);
            Assert.Equal(JobEnqueueAction.Inserted, reinserted.Action);
            await tx.CommitAsync(ct);
        }

        Assert.NotNull(await Db.From<Job>().Where(j => j.DeduplicationKey == key).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A null transaction throws ArgumentNullException before any work")]
    public async Task Null_transaction_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Jobs.EnqueueAsync((DbTransaction)null!, Request(), ct));
    }

    [Fact(
        DisplayName = "A detached transaction is rejected with the shared committed-rolled-back-or-disposed message and a disposed one also fails"
    )]
    public async Task Detached_or_disposed_transaction_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await Db.OpenConnectionAsync(ct);

        // A detached transaction reports no connection, so it surfaces the shared message that enumerates
        // committed, rolled back, or disposed rather than a state-specific one. Modeled with a null
        // connection because a real provider transaction either keeps its connection or throws its own
        // disposed error, so this is the only deterministic way to reach Acta's detached branch.
        await using (var detached = new WrappingCallerTransaction(null))
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.EnqueueAsync(detached, Request(), ct));
            Assert.Equal("transaction", ex.ParamName);
            Assert.Contains("committed, rolled back, or disposed", ex.Message, StringComparison.Ordinal);
        }

        // A real disposed transaction also fails: some providers surface their own disposed error from the
        // connection accessor before Acta inspects it, so assert only that it is rejected.
        var disposed = await conn.BeginTransactionAsync(ct);
        await disposed.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await Jobs.EnqueueAsync(disposed, Request(), ct));
    }

    [Fact(DisplayName = "A transaction bound to a foreign provider connection is rejected as a provider mismatch")]
    public async Task Wrong_provider_transaction_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var foreign = new ForeignCallerConnection(ConnectionState.Open);
        await using var tx = new WrappingCallerTransaction(foreign);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.EnqueueAsync(tx, Request(), ct));
        Assert.Equal("transaction", ex.ParamName);
        Assert.Contains("provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A transaction on a closed connection of the right provider is rejected as not open")]
    public async Task Closed_connection_transaction_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        // A real provider connection (so the provider match passes) that has been closed, wrapped so the
        // transaction still reports it - the connection-not-open branch fires before any command.
        var conn = await Db.OpenConnectionAsync(ct);
        await conn.CloseAsync();
        await using var tx = new WrappingCallerTransaction(conn);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.EnqueueAsync(tx, Request(), ct));
        Assert.Equal("transaction", ex.ParamName);
        Assert.Contains("not open", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An enqueue rejection inside the caller transaction requires full caller rollback and persists nothing")]
    public async Task Enqueue_rejection_requires_full_caller_rollback()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = TestKey("txn-reject");

        await using var conn = await Db.OpenConnectionAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        var dupKey = TestKey("txn-batch-dup");
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, marker, ct);

            // A same-batch duplicate deduplication key is a deterministic pre-SQL rejection. Acta throws
            // the translated type once (no retry) and the caller must roll back the whole transaction.
            IReadOnlyList<JobEnqueueRequest> batch = [Request(key: dupKey), Request(key: dupKey)];
            await Assert.ThrowsAsync<DuplicateDeduplicationKeyInBatchException>(async () => await Jobs.EnqueueBatchAsync(tx, batch, ct));

            await tx.RollbackAsync(ct);
        }

        Assert.Equal(0, await CountBusinessRowsAsync(conn, probe, marker, ct));
        var jobCount = await Db.From<Job>().Where(j => j.DeduplicationKey == dupKey).CountAsync(ct);
        Assert.Equal(0, jobCount);
    }

    [Fact(DisplayName = "A transactional enqueue publishes no wakeup while the owned path publishes one")]
    public async Task Transactional_enqueue_publishes_no_wakeup()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await Db.OpenConnectionAsync(ct);

        var before = _wakeup.Count;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await Jobs.EnqueueAsync(tx, Request(), ct);
            await tx.CommitAsync(ct);
        }
        Assert.Equal(before, _wakeup.Count);

        // Control: the Acta-owned enqueue of a due job publishes exactly one wakeup, proving the seam works.
        await Jobs.EnqueueAsync(Request(), ct);
        Assert.True(_wakeup.Count > before);
    }

    private JobEnqueueRequest Request(string jobName = "add-numbers", string? key = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 1));
        return new JobEnqueueRequest(TestNamespace, jobName, payload, DeduplicationKey: key);
    }

    private static async Task InsertBusinessRowAsync(DbConnection conn, DbTransaction tx, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {probe} (marker) VALUES (@marker)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@marker";
        p.Value = marker;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountBusinessRowsAsync(DbConnection conn, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {probe} WHERE marker = @marker";
        var p = cmd.CreateParameter();
        p.ParameterName = "@marker";
        p.Value = marker;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    // Records every published wakeup so the no-wake fact can compare counts across a transactional and an
    // owned enqueue. The wait side is never exercised in an enqueue-only spec.
    private sealed class RecordingWakeup : IWorkerWakeup
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException("Enqueue-only spec never waits on a wakeup.");
    }
}

/// <summary>
/// A minimal foreign <see cref="DbConnection"/> for the transactional-enqueue structural-rejection facts:
/// its concrete type matches no configured Acta provider, so the caller-transaction validation rejects it
/// as a provider mismatch. <see cref="State"/> is the only member the validation reads; the rest throw.
/// </summary>
internal sealed class ForeignCallerConnection(ConnectionState state) : DbConnection
{
    public override ConnectionState State => state;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";

    public override string Database => "";

    public override string DataSource => "";

    public override string ServerVersion => "";

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() { }

    public override void Open() => throw new NotSupportedException();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}

/// <summary>
/// A <see cref="DbTransaction"/> that reports whatever connection it was handed, used to reach the
/// caller-transaction validation branches that a real provider transaction cannot construct in a test:
/// a foreign-provider connection, and a right-provider connection that has been closed. Commit and
/// rollback are no-ops - the validation fails before any of that is reached.
/// </summary>
internal sealed class WrappingCallerTransaction(DbConnection? connection) : DbTransaction
{
    protected override DbConnection? DbConnection => connection;

    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;

    public override void Commit() { }

    public override void Rollback() { }
}
