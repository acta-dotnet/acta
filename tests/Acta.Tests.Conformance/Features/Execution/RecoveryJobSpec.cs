using System.Diagnostics;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// The <c>sys.recovery</c> handler itself, driven the way production reaches it: the recurring slot is
/// claimed by the runtime and the generated dispatch invokes the handler. The pieces it composes have
/// their own specs; what this one pins is the composition - one tick reclaims the namespace's
/// lease-expired jobs, backstops a child latch whose raise was lost to a crash, and publishes the two
/// wakeups that tell workers the reclaimed rows and the released parent are claimable now.
/// </summary>
[ConformanceSpec(
    "execution.recovery-job",
    "One sys.recovery tick reclaims, releases, and wakes",
    Area = "Recovery",
    Contract = "One sys.recovery tick returns lease-expired jobs to Ready, re-raises child latches lost to a crash, and wakes the workers that can claim them.",
    Arrange = "A claimed job's lease is lapsed and a terminal child's latch raise is lost, in the namespace whose sys.recovery slot is due.",
    Act = "The runtime claims the due sys.recovery slot by id, and the generated dispatch invokes its handler.",
    Assert = "The stranded job is Ready again, the waiting parent is released, and both the namespace and the all-namespaces wakeup are published."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.MarkDeadWorkersAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.GetStaleChildLatchesAsync))]
public abstract class RecoveryJobSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Claim with a negative TTL so the lease lands in the past, deterministically simulating a worker
    // that claimed then died long enough ago for its lease to lapse.
    private const int LeaseTtlSeconds = -5;

    private readonly RecordingWakeup _wakeups = new();

    // The handler under test is the framework's own recurring slot, so it has to be registered, and it
    // has to be allowed to keep the next_run_at_utc a fire leaves behind.
    protected override bool RegisterSystemJobs => true;
    protected override bool ParkScheduleSlots => false;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private IExecutionStore Execution => Services.GetRequiredService<IExecutionStore>();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the wakeups the handler publishes are read
        // from the same transport the runtime resolves.
        services.AddSingleton<IWorkerWakeup>(_wakeups);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(
        DisplayName = "A due sys.recovery tick reclaims the stranded job, raises the latch its crashed child never set, and wakes both channels"
    )]
    public async Task Due_tick_reclaims_releases_and_wakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, TestNamespaceId, ct);

        var stranded = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        Assert.Single(await Execution.ClaimOneAsync(TestNamespaceId, workerId, LeaseTtlSeconds, stranded, ct));

        // A parent Suspended on a child that landed terminal with its latch never set: the raise lost to
        // a crash between the two writes, which only the handler's backstop pass can repair.
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<Job>().Where(j => j.ParentId == parent.JobId).ToListAsync(ct));
        await SetTerminalWithoutRaiseAsync(child.Id, ct);

        var slotId = await RecoverySlotIdAsync(ct);
        var before = _wakeups.Published.Count;

        await DriveRecoveryUntilSettledAsync(slotId, stranded.JobId, parent.JobId, ct);

        var reclaimed = await ReadJobAsync(stranded.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal((short)1, reclaimed.FailureCount);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(JobCheckpointStatusCode.Set, Assert.Single(await ReadSignalsAsync(parent.JobId, ct)).Status);

        var published = _wakeups.Published.Skip(before).Select(c => c.Kind).ToList();
        Assert.Contains(WorkerWakeupChannelKind.WorkerNamespace, published);
        Assert.Contains(WorkerWakeupChannelKind.AllWorkerNamespaces, published);
    }

    /// <summary>
    /// Forces the slot due and runs it, until both repairs have landed. <c>reclaim_stuck_jobs</c> reads
    /// its stuck set with READPAST, so one tick can skip a row another spec momentarily holds in the
    /// shared schema - the same reason <see cref="RecoverySweep"/> exists, one level up.
    /// </summary>
    private async Task DriveRecoveryUntilSettledAsync(long slotId, long strandedId, long parentId, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            await ForceSlotDueAsync(slotId, ct);
            Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(slotId, ct));

            var settled =
                (await ReadJobAsync(strandedId, ct)).Status == JobStatusCode.Ready
                && (await ReadJobAsync(parentId, ct)).Status == JobStatusCode.Ready;
            if (settled || Stopwatch.GetElapsedTime(started) >= SpecWaits.Converge)
            {
                return;
            }
        }
    }

    private Task ForceSlotDueAsync(long slotId, CancellationToken ct) =>
        Db.From<JobRuntime>()
            .Where(r => r.Id == slotId)
            .UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1) }, ct);

    /// <summary>Lands the child terminal the way a crash does: the status moves, the latch is never set.</summary>
    private Task SetTerminalWithoutRaiseAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Cancelled),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );

    private async Task<long> RecoverySlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "sys.recovery"), ct);
        Assert.NotNull(id);
        return id!.Value;
    }
}
