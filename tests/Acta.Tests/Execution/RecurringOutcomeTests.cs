using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit-pins <see cref="JobExecution.ComputeRecurringOutcome"/>: a recurring slot re-arms Ready on failure
/// regardless of the consecutive-failure count (MaxAttempts is the one-off budget only), the counter
/// saturates at <c>short.MaxValue</c>, a success resets it to zero, and an exhausted schedule pauses.
/// </summary>
public class RecurringOutcomeTests
{
    private static readonly DateTime NextRun = new(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void Failed_past_max_attempts_re_arms_ready_not_failed()
    {
        // failureCount already well past the budget: a one-off would land terminal Failed here; a
        // recurring slot re-arms Ready and just bumps the counter.
        var (status, failureCount, reason, message) = JobExecution.ComputeRecurringOutcome(
            ExecutionOutcome.Failed,
            Job(failureCount: 5),
            Descriptor(maxAttempts: 2),
            Fire(NextRun),
            JobEventReasonCode.JobUnhandledException,
            "boom"
        );

        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal((short)6, failureCount);
        Assert.Null(reason);
        Assert.Null(message);
    }

    [Fact]
    public void Failure_counter_saturates_at_short_max_value()
    {
        var (_, failureCount, _, _) = JobExecution.ComputeRecurringOutcome(
            ExecutionOutcome.Failed,
            Job(failureCount: short.MaxValue),
            Descriptor(maxAttempts: 2),
            Fire(NextRun),
            JobEventReasonCode.JobUnhandledException,
            "boom"
        );

        Assert.Equal(short.MaxValue, failureCount);
    }

    [Fact]
    public void Succeeded_resets_the_failure_counter_to_zero()
    {
        var (status, failureCount, _, _) = JobExecution.ComputeRecurringOutcome(
            ExecutionOutcome.Succeeded,
            Job(failureCount: 9),
            Descriptor(maxAttempts: 2),
            Fire(NextRun),
            null,
            null
        );

        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal((short)0, failureCount);
    }

    [Fact]
    public void Exhausted_schedule_pauses_with_schedules_exhausted()
    {
        var (status, _, reason, _) = JobExecution.ComputeRecurringOutcome(
            ExecutionOutcome.Succeeded,
            Job(failureCount: 0),
            Descriptor(maxAttempts: 2),
            Fire(null),
            null,
            null
        );

        Assert.Equal(JobStatusCode.Paused, status);
        Assert.Equal(JobEventReasonCode.JobSchedulesExhausted, reason);
    }

    private static RecurringFireOutcome Fire(DateTime? nextRun) => new([], [], nextRun);

    private static ClaimedJob Job(short failureCount) =>
        new(
            JobId: 1,
            JobRef: Guid.Empty,
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
            LeaseExpiresAtUtc: default,
            CreatedAtUtc: default,
            FailureCount: failureCount,
            Version: 1
        );

    private static JobDescriptor Descriptor(short maxAttempts) =>
        new(
            JobName: "recurring-unit",
            HandlerType: typeof(RecurringOutcomeTests),
            MethodName: "N/A",
            InputType: typeof(NoInput),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: true,
            RequiresCancellationToken: true,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: maxAttempts,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: JobAlertProfileCode.OnFailure,
            Invoker: static async (_, _, _, _) =>
            {
                await Task.CompletedTask;
                return new JobHandlerInvocationResult(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
        );
}
