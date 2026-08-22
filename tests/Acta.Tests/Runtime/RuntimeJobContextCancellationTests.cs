using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Services.Locks;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RuntimeJobContextCancellationTests
{
    [Fact]
    public async Task RunStepAsync_propagates_linked_token_cancellation_without_completing_step_failure()
    {
        var db = new StepCancellationExecutionStore();
        var ctx = new RuntimeJobContext(
            new ClaimedJob(
                JobId: 42,
                JobRef: Guid.CreateVersion7(),
                NamespaceId: 1,
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                DeduplicationKey: null,
                CorrelationKey: null,
                ExclusiveKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0,
                Version: 1
            ),
            jobName: "step-host",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobStore: null!,
            signalStore: null!,
            alerts: null!,
            executionStore: db,
            new ThrowingSerializerRegistry(),
            new ThrowingLockStore(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunStepAsync("cancelled-step", token => Task.FromCanceled(token), ct: callerCts.Token)
        );

        Assert.False(db.CompleteStepCalled);
    }

    // Same as above, but through the positional-ct convenience overload: proves the forwarder
    // preserves the linked-token cancellation semantics of the canonical configure-taking overload
    // rather than only the compile-time shape.
    [Fact]
    public async Task RunStepAsync_positional_ct_overload_propagates_linked_token_cancellation_without_completing_step_failure()
    {
        var db = new StepCancellationExecutionStore();
        var ctx = new RuntimeJobContext(
            new ClaimedJob(
                JobId: 42,
                JobRef: Guid.CreateVersion7(),
                NamespaceId: 1,
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                DeduplicationKey: null,
                CorrelationKey: null,
                ExclusiveKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0,
                Version: 1
            ),
            jobName: "step-host",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobStore: null!,
            signalStore: null!,
            alerts: null!,
            executionStore: db,
            new ThrowingSerializerRegistry(),
            new ThrowingLockStore(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunStepAsync("cancelled-step", token => Task.FromCanceled(token), callerCts.Token)
        );

        Assert.False(db.CompleteStepCalled);
    }

    // Only StartStep and CompleteStep are exercised: StartStep yields an Invoke decision so the body
    // runs, and CompleteStep must never be reached once the caller token is already cancelled.
    private sealed class StepCancellationExecutionStore : IExecutionStore
    {
        public bool CompleteStepCalled { get; private set; }

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
            Task.FromResult(
                new StartStepDecision(
                    StartStepOutcomeCode.Invoke,
                    AttemptNumber: 1,
                    Version: 1,
                    NextRetryAtUtc: null,
                    ResultFormatId: 0,
                    Result: null,
                    ReasonCode: null,
                    ReasonMessage: null
                )
            );

        public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct)
        {
            CompleteStepCalled = true;
            throw new InvalidOperationException("CompleteStep must not run for caller cancellation.");
        }

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordJobNoteAsync(long jobId, string message, JobPayload? detail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Acta.Runtime.Modules.Execution.ChildLatches.StaleChildLatch>> GetStaleChildLatchesAsync(
            int namespaceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Acta.Runtime.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
            ArmOrConsumeSleepTimerCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();

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

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSerializerRegistry : IJobPayloadSerializerRegistry
    {
        public IJobPayloadSerializer Resolve(byte formatId) => throw new NotSupportedException();

        public bool IsRegistered(byte formatId) => false;
    }

    private sealed class ThrowingLockStore : ILockStore
    {
        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct) => throw new NotSupportedException();
    }
}
