using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The heartbeat against a job that has a buffered Bulk completion pending. The renewal answers job ids
/// only, so the heartbeat cannot tell which execution a pending entry speaks for; it hands the row to the
/// orphan release, which reads the execution number and leaves the row alone only when it matches.
/// Excluding the job by id here would let a completion for an older execution shield a newer one.
/// </summary>
public sealed class WorkerHeartbeatPendingCompletionTests
{
    [Fact]
    public async Task A_row_with_a_pending_completion_is_still_handed_to_the_release_that_can_read_its_execution()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        context.PendingCompletions[(9, 1)] = new BufferedCompletion(
            new CompleteExecutionRequest(9, 1, 1, ExecutionOutcome.Succeeded, 0, ReadOnlyMemory<byte>.Empty),
            "orders",
            "charge",
            9,
            ResultBytes: 0
        );
        var handed = new TaskCompletionSource<IReadOnlyList<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new WorkerHeartbeat(
            new LiveWorkerStore([9]),
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
        await heartbeat.TickAsync(ct);

        // A hang guard, not a measurement: the second tick starts the release on its own task.
        Assert.Equal([9L], await handed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    private sealed class LiveWorkerStore(IReadOnlyList<long> liveJobIds) : IWorkerStore
    {
        public Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct) =>
            Task.FromResult(liveJobIds);

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) => throw new NotSupportedException();
    }
}
