using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit-pins the failure count's saturation at 32,767 on the completion side: the next count, a slot's final
/// failure, the one-shot retry plan, and the MaxAttempts cap that keeps a saturated count able to spend any budget.
/// The reclaim side is pinned per provider by <c>ReclaimStuckJobsSpec</c>.
/// </summary>
public class FailureCountSaturationTests
{
    private const int Ceiling = 32_767;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(Ceiling - 1, Ceiling)]
    [InlineData(Ceiling, Ceiling)]
    public void The_next_failure_count_climbs_until_the_ceiling_and_stays(int failureCount, int expected)
    {
        Assert.Equal(Ceiling, WorkerFinalizer.FailureCountCeiling);
        Assert.Equal(expected, WorkerFinalizer.NextFailureCount(failureCount));
    }

    [Theory]
    [InlineData(Ceiling - 1, Ceiling)]
    [InlineData(Ceiling, Ceiling)]
    public void A_failed_firing_at_the_ceiling_returns_the_slot_to_idle_without_climbing(int failureCount, int expected)
    {
        var failed = new ExecutionCompletion(CompletionKind.Failed) { Reason = EventReason.UnhandledException };
        var slot = Job(failureCount) with { Kind = JobKindCode.PermanentSlot };

        var command = WorkerFinalizer.BuildCommand(Descriptor(1), slot, failed, WorkerFinalizer.PlanRetry(Descriptor(1), slot, failed));

        Assert.Equal(JobStatusCode.Idle, command.FinalStatus);
        Assert.Equal(expected, command.FailureCount);
    }

    [Theory]
    [InlineData(Ceiling - 2, true)]
    [InlineData(Ceiling - 1, false)]
    [InlineData(Ceiling, false)]
    public void A_saturated_count_still_spends_the_largest_budget(int failureCount, bool inBudget)
    {
        var failed = new ExecutionCompletion(CompletionKind.Failed) { Reason = EventReason.UnhandledException };

        var (attempted, _, planned) = WorkerFinalizer.PlanRetry(Descriptor(Ceiling), Job(failureCount), failed);

        Assert.Equal(Math.Min(failureCount + 1, Ceiling), attempted);
        Assert.Equal(inBudget, planned);
    }

    [Theory]
    [InlineData(Ceiling, true)]
    [InlineData(Ceiling + 1, false)]
    public void MaxAttempts_is_capped_at_the_ceiling(int maxAttempts, bool accepted)
    {
        var validate = () => JobDefinitionRegistration.ValidateDescriptorShape(Descriptor(maxAttempts), "test");

        if (accepted)
        {
            validate();
        }
        else
        {
            Assert.Contains("between 1 and 32767", Assert.Throws<ArgumentException>(validate).Message);
        }
    }

    private static JobDescriptor Descriptor(int maxAttempts) =>
        new(
            "probe",
            typeof(FailureCountSaturationTests),
            MethodName: "Run",
            InputType: typeof(object),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: maxAttempts,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        )
        {
            Backoff = "0s",
        };

    private static ClaimedJob Job(int failureCount) =>
        new(
            JobId: 1,
            JobRef: Guid.Empty,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 1,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            CreatedAtUtc: default,
            FailureCount: failureCount
        );
}
