using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers the drain-reconcile race: <see cref="WorkerHeartbeat.TickAsync"/> must reconcile only the
/// running attempts that existed BEFORE the lease-extend queries ran, not whatever is in
/// <c>RunningAttempts</c> by the time the extend queries return. Before the fix, a job claimed and
/// dispatched into <c>RunningAttempts</c> while an extend query is still in flight was invisible to
/// that query's "live" result (it was claimed too late to be extended) but present in the dictionary
/// <see cref="WorkerHeartbeat.ReconcileAttemptsAsync"/> enumerates afterwards, so it was wrongly
/// cancelled. The fix snapshots <c>RunningAttempts</c> before the extend loop and reconciles only that
/// snapshot; an attempt registered mid-tick is simply picked up by the next tick instead.
/// </summary>
public sealed class WorkerHeartbeatReconcileSnapshotTests
{
    [Fact]
    public async Task TickAsync_does_not_cancel_an_attempt_registered_while_the_extend_query_is_in_flight()
    {
        var db = new GatedWorkerStore(liveJobIds: [1]);
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });

        var heartbeat = new WorkerHeartbeat(db, options, registration, context, NullLogger.Instance);

        var ct = TestContext.Current.CancellationToken;
        var tickTask = heartbeat.TickAsync(ct);

        // Wait until the extend query is actually in flight (blocked on the gate) before registering
        // the new attempt, so it lands squarely in the gap the race exploits.
        await db.Entered.WaitAsync(TimeSpan.FromSeconds(5), ct);

        using var lateAttemptCts = new CancellationTokenSource();
        var lateAttempt = new RunningAttempt(lateAttemptCts);
        context.RunningAttempts[2] = lateAttempt;

        db.Release();
        await tickTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.False(lateAttemptCts.IsCancellationRequested);
    }

    // Blocks the extend call on a gate so the test can register a new RunningAttempt while the
    // tick is mid-flight, then releases it to return a fixed "live" id set. Every other member
    // throws NotSupportedException - WorkerHeartbeat.TickAsync only ever extends leases.
    private sealed class GatedWorkerStore(IReadOnlyList<long> liveJobIds) : IWorkerStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _gate.SetResult();

        public async Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        )
        {
            _entered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return liveJobIds;
        }

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(short namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) => throw new NotSupportedException();
    }
}
