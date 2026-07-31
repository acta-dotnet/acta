using Acta.Configuration;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Checkpoints;
using Acta.Modules.Execution.Workers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The bulk-completion byte threshold survives batches whose result bytes sum past Int32.MaxValue:
/// the long accumulator trips the flush at the threshold instead of wrapping negative and silently
/// disabling it for the rest of the batch.
/// </summary>
public sealed class CompletionSinkByteThresholdTests
{
    [Fact]
    public async Task Byte_sum_past_int_max_still_trips_the_flush_threshold()
    {
        var store = new RecordingExecutionStore();
        var sink = new CompletionSink(
            store,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            Options.Create(
                new JobsOptions
                {
                    BatchCompletionSize = 100,
                    BatchCompletionMaxBytes = int.MaxValue,
                    BatchCompletionInterval = TimeSpan.FromSeconds(30),
                }
            )
        );

        // Two of these overflow an int sum; an int accumulator would go negative after the second
        // item, keep the threshold "unreached", and group all three into one batch.
        const int perItem = int.MaxValue / 2 + 1;
        for (var jobId = 1L; jobId <= 3; jobId++)
        {
            await sink.EnqueueAsync(new BufferedCompletion(Request(jobId), "ns", jobId, perItem));
        }
        sink.CompleteWriter();
        await sink.RunFlusherAsync();

        Assert.Equal(3, store.BatchSizes.Sum());
        Assert.All(store.BatchSizes, size => Assert.True(size <= 2, $"byte threshold did not flush: batch of {size}"));
    }

    private static CompleteExecutionRequest Request(long jobId) =>
        new(jobId, WorkerId: 1, ExpectedExecutionNumber: 1, Outcome: default!, ResultFormatId: 0, Result: ReadOnlyMemory<byte>.Empty);

    private sealed class RecordingExecutionStore : IExecutionStore
    {
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
            IReadOnlyList<CompleteExecutionRequest> requests,
            CancellationToken ct
        )
        {
            BatchSizes.Add(requests.Count);
            IReadOnlyList<bool> finalized = [.. requests.Select(_ => true)];
            return Task.FromResult(finalized);
        }

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

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(short namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Acta.Modules.Execution.ChildLatches.StaleChildLatch>> GetStaleChildLatchesAsync(
            short namespaceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Acta.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
            ArmOrConsumeSleepTimerCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
