using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RuntimeJobContextCancellationTests
{
    [Fact]
    public async Task RunStepAsync_propagates_linked_token_cancellation_without_completing_step_failure()
    {
        var db = StepCancellationSession();
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
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0
            ),
            jobName: "step-host",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobOperations: null!,
            signals: null!,
            alerts: null!,
            executionStore: db.Services().GetRequiredService<ExecutionOperations>(),
            new ThrowingSerializerRegistry(),
            ScriptedSession.Unreachable.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunStepAsync("cancelled-step", token => Task.FromCanceled(token), ct: callerCts.Token)
        );

        Assert.DoesNotContain(db.Calls, call => call.Stem == "CompleteStep");
    }

    // Same as above, but through the positional-ct convenience overload: proves the forwarder
    // preserves the linked-token cancellation semantics of the canonical configure-taking overload
    // rather than only the compile-time shape.
    [Fact]
    public async Task RunStepAsync_positional_ct_overload_propagates_linked_token_cancellation_without_completing_step_failure()
    {
        var db = StepCancellationSession();
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
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0
            ),
            jobName: "step-host",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobOperations: null!,
            signals: null!,
            alerts: null!,
            executionStore: db.Services().GetRequiredService<ExecutionOperations>(),
            new ThrowingSerializerRegistry(),
            ScriptedSession.Unreachable.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunStepAsync("cancelled-step", token => Task.FromCanceled(token), callerCts.Token)
        );

        Assert.DoesNotContain(db.Calls, call => call.Stem == "CompleteStep");
    }

    // Only StartStep and CompleteStep are exercised: StartStep yields an Invoke decision so the body
    // runs, and CompleteStep must never be reached once the caller token is already cancelled.
    private static ScriptedSession StepCancellationSession() =>
        new(call =>
            call.Stem switch
            {
                "StartStep" => new StartStepResponse(
                    StartStepOutcomeCode.Invoke,
                    AttemptNumber: 1,
                    Version: 1,
                    NextRetryAtUtc: null,
                    ResultFormatId: 0,
                    Result: null,
                    ReasonCode: null,
                    ReasonMessage: null
                ),
                "CompleteStep" => throw new InvalidOperationException("CompleteStep must not run for caller cancellation."),
                _ => throw new NotSupportedException(call.Stem),
            }
        );

    private sealed class ThrowingSerializerRegistry : IJobPayloadSerializerRegistry
    {
        public IJobPayloadSerializer Resolve(byte formatId) => throw new NotSupportedException();

        public bool IsRegistered(byte formatId) => false;
    }
}
