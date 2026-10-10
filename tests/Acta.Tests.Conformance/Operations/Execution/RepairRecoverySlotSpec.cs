using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for <c>repair_recovery_slot</c>, called directly. The monitor that drives it, and the race
/// with a renewing heartbeat, are specified in <c>RecoverySlotMonitorSpec</c>.
/// </summary>
[ConformanceSpec(
    "repair-recovery-slot.guard",
    "A recovery slot is re-armed only when it is stranded in its own namespace",
    Area = "Recovery",
    Contract = "The repair re-arms the namespace's recovery slot Ready and due when its lease has lapsed in flight, and otherwise reports it healthy or missing untouched."
)]
public sealed class RepairRecoverySlotSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The slot under test is the framework's own; it exists only when system jobs register.
    protected override bool RegisterSystemJobs => true;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A slot in flight under a lapsed lease is re-armed Ready and due now, with the lost attempt recorded once")]
    public async Task Lapsed_slot_is_re_armed_due_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, DateTime.UtcNow.AddMinutes(-5), ct);
        var before = await RuntimeAsync(slotId, ct);

        Assert.Equal(RepairRecoverySlotOutcomeCode.Repaired, (await Execution.RepairRecoverySlotAsync(NamespaceId, slotId, ct)).Repair);

        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var after = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, after.Status);
        Assert.True(after.NextRunAtUtc <= dbNow, $"the re-armed slot is due at {after.NextRunAtUtc:O}, after {dbNow:O}");
        Assert.Null(after.LeasedByWorkerId);
        Assert.Null(after.LeaseExpiresAtUtc);
        Assert.Equal(before.FailureCount + 1, after.FailureCount);
        Assert.Equal(before.ExecutionNumber, after.ExecutionNumber);
        Assert.Equal(before.Version + 1, after.Version);

        var lost = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished).ToListAsync(ct)
        );
        Assert.Equal(ActorCode.Sys, lost.ActorCode);
        Assert.Equal(before.ExecutionNumber, lost.ExecutionNumber);
        Assert.Equal(ExecutionStatusCode.Orphaned, lost.ExecutionStatus);
        Assert.Equal(EventReasonCode.JobLeaseExpired, lost.ReasonCode);
        Assert.Equal(JobStatusCode.Executing, lost.FromStatus);
        Assert.Equal(JobStatusCode.Ready, lost.ToStatus);
    }

    [Fact(DisplayName = "A slot under a live lease, or not in flight at all, is reported healthy and left as it was")]
    public async Task Live_or_idle_slot_is_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);

        var idle = await RuntimeAsync(slotId, ct);
        Assert.Equal(RepairRecoverySlotOutcomeCode.Healthy, (await Execution.RepairRecoverySlotAsync(NamespaceId, slotId, ct)).Repair);
        AssertSame(idle, await RuntimeAsync(slotId, ct));

        await StrandAsync(slotId, DateTime.UtcNow.AddMinutes(5), ct);
        var live = await RuntimeAsync(slotId, ct);
        Assert.Equal(RepairRecoverySlotOutcomeCode.Healthy, (await Execution.RepairRecoverySlotAsync(NamespaceId, slotId, ct)).Repair);
        AssertSame(live, await RuntimeAsync(slotId, ct));

        Assert.Empty(
            await Db.From<EventEntity>().Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished).ToListAsync(ct)
        );
    }

    [Fact(DisplayName = "A lapsed slot named under a namespace that does not hold it is reported missing and stays stranded")]
    public async Task Slot_named_under_another_namespace_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await RecoverySlotIdAsync(ct);
        await StrandAsync(slotId, DateTime.UtcNow.AddMinutes(-5), ct);
        var stranded = await RuntimeAsync(slotId, ct);

        Assert.Equal(RepairRecoverySlotOutcomeCode.Missing, (await Execution.RepairRecoverySlotAsync(int.MaxValue, slotId, ct)).Repair);

        AssertSame(stranded, await RuntimeAsync(slotId, ct));
        Assert.Empty(
            await Db.From<EventEntity>().Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished).ToListAsync(ct)
        );
    }

    private static void AssertSame(RuntimeEntity expected, RuntimeEntity actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.FailureCount, actual.FailureCount);
        Assert.Equal(expected.LeasedByWorkerId, actual.LeasedByWorkerId);
    }

    private async Task<long> RecoverySlotIdAsync(CancellationToken ct)
    {
        var id = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "sys.recovery"), ct))?.JobId;
        Assert.NotNull(id);
        return id!.Value;
    }

    /// <summary>Leaves the slot as a dead worker would: Executing, owned by a real worker row, lease as given.</summary>
    private async Task StrandAsync(long jobId, DateTime leaseExpiresAtUtc, CancellationToken ct)
    {
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == NamespaceId).FirstOrDefaultAsync(ct);
        Assert.NotNull(worker);
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET status_code = @p_status, execution_number = execution_number + 1, "
                    + "leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
                ct,
                ("@p_status", (byte)JobStatusCode.Executing),
                ("@p_worker", worker!.Id),
                ("@p_expires", leaseExpiresAtUtc),
                ("@p_id", jobId)
            )
        );
    }

    private async Task<RuntimeEntity> RuntimeAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<RuntimeEntity>().Where(r => r.Id == jobId).ToListAsync(ct));
}
