using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit-pins <see cref="WorkerFinalizer.ComputeRecurringOutcome"/>: a recurring slot re-arms Ready on failure
/// regardless of the consecutive-failure count (MaxAttempts is the one-off budget only) while still carrying
/// the attempt's reason through, a success resets the counter to zero,
/// and an exhausted schedule pauses.
/// </summary>
public class RecurringOutcomeTests
{
    private static readonly DateTime NextRun = new(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void Failed_past_max_attempts_re_arms_ready_not_failed()
    {
        // failureCount already well past the budget: a one-off would land terminal Failed here; a
        // recurring slot re-arms Ready and just bumps the counter. Re-arming is not forgetting: the
        // attempt's reason rides along on the rollover, which is what makes the failure alertable and
        // attributable even though the status says the slot is armed again.
        var (status, failureCount, reason, message) = WorkerFinalizer.ComputeRecurringOutcome(
            CompletionKind.Failed,
            Job(failureCount: 5),
            Fire(NextRun),
            EventReasonCode.JobUnhandledException,
            "boom"
        );

        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal(6, failureCount);
        Assert.Equal(EventReasonCode.JobUnhandledException, reason);
        Assert.Equal("boom", message);
    }

    [Fact]
    public void Succeeded_resets_the_failure_counter_to_zero()
    {
        var (status, failureCount, reason, message) = WorkerFinalizer.ComputeRecurringOutcome(
            CompletionKind.Succeeded,
            Job(failureCount: 9),
            Fire(NextRun),
            null,
            null
        );

        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal(0, failureCount);

        // The rollover carries whatever reason the attempt supplied; a clean success supplies none, so
        // the reason columns stay empty and only a real failure marks the slot's timeline.
        Assert.Null(reason);
        Assert.Null(message);
    }

    [Fact]
    public void Exhausted_schedule_pauses_with_schedules_exhausted()
    {
        var (status, _, reason, _) = WorkerFinalizer.ComputeRecurringOutcome(
            CompletionKind.Succeeded,
            Job(failureCount: 0),
            Fire(null),
            null,
            null
        );

        Assert.Equal(JobStatusCode.Paused, status);
        Assert.Equal(EventReasonCode.JobSchedulesExhausted, reason);
    }

    [Fact]
    public void A_non_retryable_exception_rolls_a_fire_over_instead_of_ending_the_slot()
    {
        // A one-off job lands Failed on NotSupportedException; a fire is one failed occurrence, so the slot rolls over
        // with the reason that makes it alert, and the next occurrence runs once the code is fixed.
        var failed = new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = EventReasonCode.JobNonRetryableException,
            ReasonMessage = "Specified method is not supported.",
            HandlerStatusCode = JobStatusCode.Failed,
        };

        var command = WorkerFinalizer.BuildCommand(Descriptor(), Job(failureCount: 2), Fire(NextRun), failed, (0, 0, false));

        Assert.Null(command.HandlerStatusCode);
        Assert.Equal(JobStatusCode.Ready, command.FinalStatus);
        Assert.Equal(NextRun, command.JobNextRunAtUtc);
        Assert.Equal(3, command.FailureCount);
        Assert.Equal(EventReasonCode.JobNonRetryableException, command.EventReasonCode);
    }

    [Fact]
    public void A_handler_fail_still_ends_the_slot()
    {
        // ctx.FailAsync is the handler deciding, so it keeps stopping the whole schedule.
        var failed = new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = EventReasonCode.JobHandlerFailed,
            HandlerStatusCode = JobStatusCode.Failed,
        };

        var command = WorkerFinalizer.BuildCommand(Descriptor(), Job(failureCount: 0), Fire(NextRun), failed, (0, 0, false));

        Assert.Equal((byte)JobStatusCode.Failed, command.HandlerStatusCode);
        Assert.Null(command.FinalStatus);
    }

    private static RecurringFirePlan Fire(DateTime? nextRun) => new([], [], nextRun);

    // BuildCommand reads the retention policy and the result cap; nothing else of the descriptor reaches a fire.
    private static JobDescriptor Descriptor() =>
        new(
            "nightly",
            typeof(RecurringOutcomeTests),
            MethodName: "Run",
            InputType: typeof(object),
            OutputType: null,
            InputPayloadFormat: default,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    private static ClaimedJob Job(int failureCount) =>
        new(
            JobId: 1,
            JobRef: Guid.Empty,
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
            LeaseExpiresAtUtc: default,
            CreatedAtUtc: default,
            FailureCount: failureCount
        );
}
