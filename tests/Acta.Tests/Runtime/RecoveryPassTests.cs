using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The recovery pass over fakes, for the two facts a database test cannot show cheaply: the
/// dead-worker window is handed to the store rounded up, and each wakeup is published only when its
/// own half of the pass changed something.
/// </summary>
public sealed class RecoveryPassTests
{
    [Fact]
    public async Task A_fractional_dead_after_window_is_handed_to_the_store_rounded_up()
    {
        var workers = new FakeWorkerStore();
        // 45.1s of heartbeat derives 315.7s; flooring it would tombstone a live worker early.
        var pass = Pass(workers, new FakeExecutionStore(), new FakeSignalStore(), TimeSpan.FromSeconds(315.7));

        await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(316, workers.DeadAfterSeconds);
    }

    [Fact]
    public async Task A_pass_that_changes_nothing_publishes_no_wakeup()
    {
        var wakeups = new RecordingWakeup();
        var pass = Pass(new FakeWorkerStore(), new FakeExecutionStore(), new FakeSignalStore(), wakeup: wakeups);

        var outcome = await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(new RecoveryPassOutcome(0, 0, 0), outcome);
        Assert.Empty(wakeups.Channels);
    }

    [Fact]
    public async Task Reclaimed_jobs_wake_the_namespace_and_a_released_parent_wakes_every_namespace()
    {
        var wakeups = new RecordingWakeup();
        var execution = new FakeExecutionStore
        {
            Reclaim = new ReclaimStuckJobsResult(Reclaimed: 2, FailedChildren: [(ChildId: 7, ParentId: 8)]),
            StaleLatches = [new StaleChildLatch(ParentJobId: 9, ChildJobId: 10, ChildStatus: JobStatusCode.Cancelled)],
        };
        var pass = Pass(new FakeWorkerStore { Marked = 3 }, execution, new FakeSignalStore { Releases = true }, wakeup: wakeups);

        var outcome = await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(new RecoveryPassOutcome(3, 2, 2), outcome);
        Assert.Equal(
            [WorkerWakeupChannelKind.WorkerNamespace, WorkerWakeupChannelKind.AllWorkerNamespaces],
            wakeups.Channels.Select(c => c.Kind)
        );
    }

    private static RecoveryPass Pass(
        FakeWorkerStore workers,
        FakeExecutionStore execution,
        FakeSignalStore signals,
        TimeSpan? deadAfter = null,
        RecordingWakeup? wakeup = null
    )
    {
        var options = new JobsOptions();
        if (deadAfter is { } window)
        {
            options.WorkerDeadAfter = window;
        }

        return new RecoveryPass(signals, workers, execution, Options.Create(options), new WorkerWakeupPublisher(wakeup ?? new()));
    }

    private sealed class RecordingWakeup : IWorkerWakeup
    {
        public List<WorkerWakeupChannel> Channels { get; } = [];

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            Channels.Add(channel);
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException("A recovery pass never waits on a wakeup.");
    }

    private sealed class FakeSignalStore : ISignalStore
    {
        public bool Releases { get; init; }

        public Task<JobControlOutcome> RaiseSignalAsync(RaiseSignalCommand command, CancellationToken ct) =>
            Task.FromResult(
                Releases
                    ? new JobControlOutcome(JobControlActionInternal.Applied, JobStatusCode.Ready, null)
                    : new JobControlOutcome(JobControlActionInternal.Rejected, null, null)
            );

        public Task<SignalWaitDecision> WaitSignalAsync(
            long jobId,
            JobCheckpointKindCode kind,
            string name,
            int? timeoutSeconds,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class FakeWorkerStore : IWorkerStore
    {
        public int Marked { get; init; }

        public int? DeadAfterSeconds { get; private set; }

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct)
        {
            DeadAfterSeconds = deadAfterSeconds;
            return Task.FromResult(Marked);
        }

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public ReclaimStuckJobsResult Reclaim { get; init; } = new(Reclaimed: 0, FailedChildren: []);

        public IReadOnlyList<StaleChildLatch> StaleLatches { get; init; } = [];

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) => Task.FromResult(Reclaim);

        public Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
            Task.FromResult(StaleLatches);

        public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StartExecutionAction> StartExecutionAsync(
            long jobId,
            int workerId,
            int expectedExecutionNumber,
            int expectedVersion,
            int leaseTtlSeconds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
            IReadOnlyList<CompleteExecutionRequest> requests,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<RecoverySlotRepair> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordJobNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<Acta.Runtime.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
            ArmOrConsumeSleepTimerCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
