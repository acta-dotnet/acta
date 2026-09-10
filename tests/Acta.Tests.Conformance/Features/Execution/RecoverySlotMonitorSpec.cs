using System.Data.Common;
using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// The reclaim sweep runs from the <c>sys.recovery</c> slot, and a worker can die holding that slot, so
/// the sweep that would free it is the one that never runs. Every worker therefore checks exactly that
/// row, at startup and on a timer, with one guarded statement. These facts pin the guard: a live lease
/// is never touched, a lapsed one is repaired once however many workers see it, the finished event
/// names the state the slot was actually in, and a worker start heals a slot that is already lapsed.
/// </summary>
[ConformanceSpec(
    "execution.recovery-slot-monitor",
    "A stranded recovery slot is repaired under a guard by any worker",
    Area = "Execution",
    Contract = "A recovery slot in flight under a lapsed lease is re-armed once by a repair whose guard reads the lease against database time inside the statement.",
    Arrange = "The namespace's sys.recovery slot is put in flight under a lease that is live or lapsed, or its row is absent.",
    Act = "The check runs, two repairs race on one lapsed slot, a renewal commits under a waiting repair, and the worker runtime initializes over a lapsed one.",
    Assert = "A live lease is untouched, a lapsed one is re-armed with one finished event naming its state, a renewal that commits first wins, and initialize re-arms."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.RepairRecoverySlotAsync))]
public abstract class RecoverySlotMonitorSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // The slot under test is the framework's own; it exists only when system jobs register.
    protected override bool RegisterSystemJobs => true;
    protected override bool ParkScheduleSlots => false;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private IExecutionStore Execution => Services.GetRequiredService<IExecutionStore>();

    [Fact(DisplayName = "A recovery slot in flight under a live lease is left alone")]
    public async Task Live_lease_is_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Executing, DateTime.UtcNow.AddMinutes(5), ct);
        var before = await RuntimeAsync(slotId, ct);

        Assert.False(await CheckAsync(slotId, ct));

        var after = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.NotNull(after.LeasedByWorkerId);
    }

    [Fact(DisplayName = "A recovery slot in flight under a lapsed lease is re-armed once, with the lost attempt recorded")]
    public async Task Lapsed_lease_is_repaired_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Executing, DateTime.UtcNow.AddMinutes(-5), ct);
        var before = await RuntimeAsync(slotId, ct);

        Assert.True(await CheckAsync(slotId, ct));

        var after = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, after.Status);
        Assert.Null(after.LeasedByWorkerId);
        Assert.Null(after.LeaseExpiresAtUtc);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal((short)(before.FailureCount + 1), after.FailureCount);

        var finished = await Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished && e.ExecutionNumber == before.ExecutionNumber)
            .ToListAsync(ct);
        var lost = Assert.Single(finished);
        Assert.Equal(ExecutionStatusCode.Orphaned, lost.ExecutionStatus);
        Assert.Equal(JobEventReasonCode.JobLeaseExpired, lost.ReasonCode);
        Assert.Equal(JobStatusCode.Executing, lost.FromStatus);
        Assert.Equal(JobStatusCode.Ready, lost.ToStatus);

        // A second check sees a Ready slot and does nothing.
        Assert.False(await CheckAsync(slotId, ct));
    }

    [Fact(DisplayName = "A slot claimed but not yet started when its worker died is recorded as lost from Dispatched")]
    public async Task Lapsed_dispatched_slot_is_recorded_from_dispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Dispatched, DateTime.UtcNow.AddMinutes(-5), ct);
        var before = await RuntimeAsync(slotId, ct);

        Assert.True(await CheckAsync(slotId, ct));

        Assert.Equal(JobStatusCode.Ready, (await RuntimeAsync(slotId, ct)).Status);
        var finished = await Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished && e.ExecutionNumber == before.ExecutionNumber)
            .ToListAsync(ct);
        var lost = Assert.Single(finished);
        Assert.Equal(JobStatusCode.Dispatched, lost.FromStatus);
        Assert.Equal(JobStatusCode.Ready, lost.ToStatus);
    }

    [Fact(DisplayName = "Two workers repairing the same lapsed slot at once commit one repair and one event")]
    public async Task Two_repairs_of_one_lapsed_slot_commit_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Executing, DateTime.UtcNow.AddMinutes(-5), ct);
        var before = await RuntimeAsync(slotId, ct);

        // Released together so the two statements contend for the row rather than run back to back.
        var start = new TaskCompletionSource();
        var repairs = Enumerable
            .Range(0, 2)
            .Select(async _ =>
            {
                await start.Task;
                return await Execution.RepairRecoverySlotAsync(TestNamespaceId, slotId, ct);
            })
            .ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(repairs);
        Assert.Single(outcomes, o => o == RecoverySlotRepair.Repaired);
        Assert.Single(outcomes, o => o == RecoverySlotRepair.Healthy);

        var finished = await Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished && e.ExecutionNumber == before.ExecutionNumber)
            .ToListAsync(ct);
        Assert.Single(finished);
    }

    [Fact(DisplayName = "A heartbeat renewal that commits while the repair waits on the row wins; the repair declines")]
    public async Task Renewal_committing_under_the_repair_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Executing, DateTime.UtcNow.AddMinutes(-5), ct);
        var before = await RuntimeAsync(slotId, ct);

        // The renewal takes the row's write lock inside an open transaction, as extend_worker_leases
        // does: lease only, no version bump. The repair then runs on its own connection and must queue
        // behind that lock; that it has not returned by the time the lock is released is the proof the
        // two overlapped in the database rather than ran back to back. It starts on a pool thread
        // because SQLite waits for the write lock synchronously, inside BEGIN IMMEDIATE; awaited inline
        // it would hold this thread, and the commit that lets it through would never be reached.
        await using var conn = await Db.OpenConnectionAsync(ct);
        RecoverySlotRepair outcome;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await RenewLeaseAsync(conn, tx, slotId, DateTime.UtcNow.AddMinutes(5), ct);

            var repair = Task.Run(() => Execution.RepairRecoverySlotAsync(TestNamespaceId, slotId, ct), ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            Assert.False(repair.IsCompleted);

            await tx.CommitAsync(ct);
            outcome = await repair;
        }

        Assert.Equal(RecoverySlotRepair.Healthy, outcome);
        var after = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.NotNull(after.LeasedByWorkerId);
        Assert.True(after.LeaseExpiresAtUtc > DateTime.UtcNow);
        Assert.Empty(
            await Db.From<JobEvent>()
                .Where(e =>
                    e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished && e.ExecutionNumber == before.ExecutionNumber
                )
                .ToListAsync(ct)
        );
    }

    [Fact(DisplayName = "A recovery slot whose row is gone is reported missing and never recreated")]
    public async Task Missing_slot_is_reported_not_recreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingId = long.MaxValue;

        Assert.Equal(RecoverySlotRepair.Missing, await Execution.RepairRecoverySlotAsync(TestNamespaceId, missingId, ct));
        Assert.False(await CheckAsync(missingId, ct));

        Assert.Empty(await Db.From<JobRuntime>().Where(r => r.Id == missingId).ToListAsync(ct));
        Assert.Empty(await Db.From<JobEvent>().Where(e => e.JobId == missingId).ToListAsync(ct));
    }

    [Fact(DisplayName = "A worker starting over a lapsed recovery slot re-arms it before the catalog is read")]
    public async Task Initialize_repairs_a_lapsed_recovery_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, JobStatusCode.Executing, DateTime.UtcNow.AddMinutes(-5), ct);

        await Runtime.InitializeAsync(ct);

        var after = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, after.Status);
        Assert.Null(after.LeasedByWorkerId);
    }

    private Task<bool> CheckAsync(long slotId, CancellationToken ct) =>
        RecoverySlotMonitor.CheckAndRepairAsync(
            Execution,
            publisher: null,
            TestNamespaceId,
            TestNamespace,
            slotId,
            NullLogger.Instance,
            ct
        );

    private async Task<long> RecoverySlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "sys.recovery"), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    /// <summary>Leaves the slot as a dead worker would: in flight, owned by a real worker row, lease as given.</summary>
    private async Task StrandAsync(long jobId, JobStatusCode status, DateTime leaseExpiresAtUtc, CancellationToken ct)
    {
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == TestNamespaceId).FirstOrDefaultAsync(ct);
        Assert.NotNull(worker);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, execution_number = execution_number + 1, "
                + "leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)status),
            ("@p_worker", worker!.Id),
            ("@p_expires", leaseExpiresAtUtc),
            ("@p_id", jobId)
        );
    }

    /// <summary>The heartbeat's write, on a caller-held transaction: the lease moves, the version does not.</summary>
    private async Task RenewLeaseAsync(DbConnection conn, DbTransaction tx, long jobId, DateTime leaseExpiresAtUtc, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {Db.Schema}.runtimes SET lease_expires_at_utc = @p_expires WHERE job_id = @p_id";
        var expires = cmd.CreateParameter();
        expires.ParameterName = "@p_expires";
        expires.Value = DbValueCoercion.Coerce(leaseExpiresAtUtc, typeof(DateTime), Db.Provider);
        cmd.Parameters.Add(expires);
        var id = cmd.CreateParameter();
        id.ParameterName = "@p_id";
        id.Value = jobId;
        cmd.Parameters.Add(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<JobRuntime> RuntimeAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == jobId).ToListAsync(ct));
}
