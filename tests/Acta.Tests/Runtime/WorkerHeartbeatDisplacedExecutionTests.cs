using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A job claimed again into a newer execution while an older attempt still runs under it in this process,
/// the claim's answer lost so no claim loop saw it. The renewal reports the row's execution, so the
/// heartbeat cancels the older attempt instead of feeding it, and hands the row to the orphan release
/// instead of counting it as running.
/// </summary>
public sealed class WorkerHeartbeatDisplacedExecutionTests
{
    [Fact]
    public async Task A_renewal_of_a_newer_execution_cancels_the_older_attempt_and_releases_the_row()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        using var staleCts = new CancellationTokenSource();
        var stale = new RunningAttempt(staleCts) { ExecutionNumber = 1 };
        var deadlineBefore = stale.JobLeaseGoodUntil;
        context.RunningAttempts[7] = stale;
        var handed = new TaskCompletionSource<IReadOnlyList<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new WorkerHeartbeat(
            new RenewingWorkerStore(new LeaseRenewalRow(7, ExecutionNumber: 2, Renewed: true)),
            Options.Create(new JobsOptions()),
            new WorkerRegistration("orders", null, null, [], []),
            context,
            NullLogger.Instance,
            (jobIds, _) =>
            {
                handed.TrySetResult(jobIds);
                return Task.CompletedTask;
            }
        );
        var ct = TestContext.Current.CancellationToken;

        await heartbeat.TickAsync(ct);

        Assert.True(staleCts.IsCancellationRequested);
        Assert.Equal(deadlineBefore, stale.JobLeaseGoodUntil);

        await heartbeat.TickAsync(ct);

        // A hang guard, not a measurement: the second tick starts the release on its own task.
        Assert.Equal([7L], await handed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    private sealed class RenewingWorkerStore(LeaseRenewalRow row) : IWorkerStore
    {
        public Task<IReadOnlyList<LeaseRenewalRow>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<LeaseRenewalRow>>([row]);

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) => throw new NotSupportedException();
    }
}
