using Acta.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The recovery pass over scripted database calls, for the two facts a database test cannot show cheaply: the
/// dead-worker window is handed to the store rounded up, and each wakeup is published only when its
/// own half of the pass changed something.
/// </summary>
public sealed class RecoveryPassTests
{
    [Fact]
    public async Task A_fractional_dead_after_window_is_handed_to_the_store_rounded_up()
    {
        var workers = WorkerSweep();
        // 45.1s of heartbeat derives 315.7s; flooring it would tombstone a live worker early.
        var pass = Pass(workers, ExecutionStore(), SignalStore(releases: false), TimeSpan.FromSeconds(315.7));

        await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(316, Assert.Single(workers.Calls).Get(ActaSchema.Sql.DeadAfterSeconds));
    }

    [Fact]
    public async Task A_pass_that_changes_nothing_publishes_no_wakeup()
    {
        var wakeups = new RecordingWakeup();
        var pass = Pass(WorkerSweep(), ExecutionStore(), SignalStore(releases: false), wakeup: wakeups);

        var outcome = await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(new RecoveryPassReport(0, 0, 0, 0), outcome);
        Assert.Empty(wakeups.Channels);
    }

    [Fact]
    public async Task Reclaimed_jobs_wake_the_namespace_and_a_released_parent_wakes_every_namespace()
    {
        var wakeups = new RecordingWakeup();
        // Two reclaimed leases, one of them a child that landed Failed under parent 8.
        var execution = ExecutionStore(
            reclaimed:
            [
                new ReclaimedJobRow(JobId: 7, JobStatusCode.Failed, ParentId: 8, LaneRepaired: false),
                new ReclaimedJobRow(JobId: 11, JobStatusCode.Ready, ParentId: null, LaneRepaired: false),
            ],
            staleLatches: [new StaleChildLatch(ParentJobId: 9, ChildJobId: 10, ChildStatus: JobStatusCode.Cancelled)]
        );
        var pass = Pass(WorkerSweep(marked: 3), execution, SignalStore(releases: true), wakeup: wakeups);

        var outcome = await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(new RecoveryPassReport(3, 2, 2, 0), outcome);
        Assert.Equal(
            [WorkerWakeupChannelKind.WorkerNamespace, WorkerWakeupChannelKind.AllWorkerNamespaces],
            wakeups.Channels.Select(c => c.Kind)
        );
    }

    [Fact]
    public async Task A_repaired_lane_alone_wakes_the_namespace()
    {
        var wakeups = new RecordingWakeup();
        var execution = ExecutionStore(
            reclaimed: [new ReclaimedJobRow(JobId: 12, JobStatusCode.Ready, ParentId: null, LaneRepaired: true)]
        );
        var pass = Pass(WorkerSweep(), execution, SignalStore(releases: false), wakeup: wakeups);

        var outcome = await pass.RunAsync(namespaceId: 1, namespaceName: "payments", TestContext.Current.CancellationToken);

        Assert.Equal(new RecoveryPassReport(0, 0, 0, 1), outcome);
        Assert.Equal([WorkerWakeupChannelKind.WorkerNamespace], wakeups.Channels.Select(c => c.Kind));
    }

    private static RecoveryPass Pass(
        ScriptedSession workers,
        Acta.Runtime.Execution execution,
        Signals signals,
        TimeSpan? deadAfter = null,
        RecordingWakeup? wakeup = null
    )
    {
        var options = new JobsOptions();
        if (deadAfter is { } window)
        {
            options.WorkerDeadAfter = window;
        }

        return new RecoveryPass(
            signals,
            workers.Services().GetRequiredService<WorkerLifecycle>(),
            execution,
            Options.Create(options),
            new WorkerWakeupPublisher(wakeup ?? new())
        );
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

    // A raise that releases its parent applies and leaves it Ready; one that does not is rejected unwritten.
    private static Signals SignalStore(bool releases) =>
        new ScriptedSession(_ =>
            releases
                ? new JobControlActionRow(ControlAction.Applied, JobStatusCode.Ready)
                : new JobControlActionRow(ControlAction.Rejected, null)
        )
            .Services()
            .GetRequiredService<Signals>();

    // The dead-worker sweep, the pass's only worker call, reports how many workers it marked.
    private static ScriptedSession WorkerSweep(int marked = 0) => new(_ => marked);

    // The reclaim answers the rows it touched and the latch read its stale latches; nothing else is reached.
    private static Acta.Runtime.Execution ExecutionStore(ReclaimedJobRow[]? reclaimed = null, StaleChildLatch[]? staleLatches = null) =>
        new ScriptedSession(call =>
            call.Stem switch
            {
                "ReclaimStuckJobs" => reclaimed ?? [],
                "GetStaleChildLatches" => staleLatches ?? [],
                _ => throw new NotSupportedException(call.Stem),
            }
        ).Services().GetRequiredService<Acta.Runtime.Execution>();
}
