using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A wait begun outside a check step's body and ended inside it got past the refusal at the call: the step refuses it
/// when the body ends with it, for good, since the resume or a retry would ask the check, which can find the body's
/// effect and skip the rest of the body.
/// </summary>
public sealed class RuntimeJobContextCheckStepTests
{
    [Fact]
    public async Task A_wait_carried_into_a_check_step_body_is_refused_and_records_nothing()
    {
        var db = new ScriptedSession(call =>
            call.Stem switch
            {
                "StartJobStep" => new StartJobStepResponse(
                    StartJobStepOutcomeCode.Invoke,
                    Version: 1,
                    ResultFormatId: 0,
                    Result: null,
                    FailureMessage: null
                ),
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var ctx = new RuntimeJobContext(
            new ClaimedJob(
                JobId: 42,
                JobRef: Guid.CreateVersion7(),
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                CorrelationKey: null,
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
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
            new JobPayloadSerializerRegistry([JsonJobPayloadSerializer.Default]),
            ScriptedSession.Unreachable.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            firing: null,
            deadlineAtUtc: null
        );
        var carried = new RescheduleJobException(TimeSpan.FromMinutes(1), reasonMessage: null);

        var refused = await Assert.ThrowsAsync<StepFailedException>(() =>
            ctx.RunStepWithCheckAsync("approve", _ => Task.FromException(carried), _ => Task.FromResult(false), CancellationToken.None)
        );

        Assert.Equal("approve", refused.StepName);
        Assert.Contains("A RescheduleJobException inside a check step's body is refused", refused.Message, StringComparison.Ordinal);
        Assert.True(NonRetryableExceptions.IsBuiltIn(refused));
        Assert.DoesNotContain(db.Calls, call => call.Stem == "CompleteJobStep");
    }
}
