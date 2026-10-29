using System.Collections.Immutable;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A worker combines the framework manifest plus every registered module into one namespace catalog.
/// The generator only checks duplicate job names inside each generated manifest, so the combined set
/// must be validated before any catalog write; a collision would otherwise register last-writer-wins
/// and dispatch an arbitrary one of the colliding handlers.
/// </summary>
public sealed class WorkerCatalogValidationTests
{
    [Fact]
    public void A_job_name_declared_by_two_manifests_fails_startup_naming_both_handlers()
    {
        var descriptors = ImmutableArray.Create(
            Descriptor("send-receipt", typeof(HandlerA)),
            Descriptor("send-receipt", typeof(HandlerB)),
            Descriptor("unrelated", typeof(HandlerA))
        );

        var ex = Assert.Throws<InvalidOperationException>(() => CatalogRegistrar.ValidateUniqueJobNames(descriptors));

        Assert.Contains("send-receipt", ex.Message);
        Assert.Contains(typeof(HandlerA).FullName!, ex.Message);
        Assert.Contains(typeof(HandlerB).FullName!, ex.Message);
        Assert.DoesNotContain("unrelated", ex.Message);
    }

    [Fact]
    public void Unique_job_names_pass()
    {
        var descriptors = ImmutableArray.Create(Descriptor("job-one", typeof(HandlerA)), Descriptor("job-two", typeof(HandlerB)));

        CatalogRegistrar.ValidateUniqueJobNames(descriptors);
    }

    [Fact]
    public void A_claiming_worker_with_zero_descriptors_fails_startup()
    {
        // A module-less worker with framework jobs disabled would claim namespace jobs it can never
        // dispatch; the claimed rows would rot until lease recovery.
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogRegistrar.ValidateHasDescriptors("billing", []));

        Assert.Contains("billing", ex.Message);
    }

    [Fact]
    public void A_worker_with_at_least_one_descriptor_passes()
    {
        CatalogRegistrar.ValidateHasDescriptors("billing", [Descriptor("job-one", typeof(HandlerA))]);
    }

    [Fact]
    public void A_scheduled_definition_with_a_deadline_fails_startup()
    {
        // A deadline anchors to job creation and a recurring slot's row lives forever, so a Strict
        // deadline would terminally cancel the slot the first time it is claimed past that anchor.
        var scheduled = Descriptor("nightly", typeof(HandlerA)) with
        {
            DeadlineSeconds = 1800,
            Schedules =
            [
                new ScheduleDescriptor(
                    "nightly",
                    "default",
                    "0 0 * * *",
                    TimeZoneId: null,
                    MisfireStrategyCode.Skip,
                    ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: []
                ),
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => CatalogRegistrar.ValidateDeadlineRequirements([scheduled]));

        Assert.Contains("nightly", ex.Message);
        Assert.Contains("Deadline", ex.Message);
    }

    [Fact]
    public void A_deadline_without_schedules_and_a_schedule_without_a_deadline_pass()
    {
        var oneShot = Descriptor("one-shot", typeof(HandlerA)) with { DeadlineSeconds = 1800 };
        var scheduled = Descriptor("nightly", typeof(HandlerB)) with
        {
            Schedules =
            [
                new ScheduleDescriptor(
                    "nightly",
                    "default",
                    "0 0 * * *",
                    TimeZoneId: null,
                    MisfireStrategyCode.Skip,
                    ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: []
                ),
            ],
        };

        CatalogRegistrar.ValidateDeadlineRequirements([oneShot, scheduled]);
    }

    [Fact]
    public void A_definition_declaring_another_delivery_than_it_registered_fails_startup_naming_a_new_job_name()
    {
        var changed = Descriptor("nightly", typeof(HandlerA)) with { ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence };
        var unchanged = Descriptor("hourly", typeof(HandlerB));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CatalogRegistrar.ValidateScheduledDelivery(
                "billing",
                [changed, unchanged],
                [Stored("nightly", ScheduledDeliveryCode.Permanent), Stored("hourly", ScheduledDeliveryCode.Permanent)]
            )
        );

        Assert.Contains("nightly", ex.Message);
        Assert.Contains("new job name", ex.Message);
        Assert.DoesNotContain("hourly", ex.Message);
    }

    [Fact]
    public void A_definition_keeping_its_delivery_or_registering_for_the_first_time_passes()
    {
        var kept = Descriptor("nightly", typeof(HandlerA)) with { ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence };
        var fresh = Descriptor("hourly", typeof(HandlerB)) with { ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence };

        CatalogRegistrar.ValidateScheduledDelivery("billing", [kept, fresh], [Stored("nightly", ScheduledDeliveryCode.PerOccurrence)]);
    }

    private static StoredDefinitionContract Stored(string name, ScheduledDeliveryCode delivery) =>
        new(
            DefinitionName: name,
            ManifestGenerationAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Contract: new DefinitionContract(
                "Input",
                null,
                JobPayloadFormat.None.Id,
                JobPayloadFormat.None.Name,
                0,
                JobPayloadFormat.NoneName
            ),
            DefinitionId: 1,
            DefinitionHash: "hash",
            Status: DefinitionStatusCode.Active,
            Effective: new EffectiveJobPolicy(
                JobPriorityCode.Normal,
                MaxAttempts: 1,
                ConcurrencyLimit: null,
                RateLimit: null,
                RateName: null,
                Backoff: JobDefinitionRegistration.DefaultBackoffExpression,
                ExecutionTimeoutSeconds: JobDefinitionRegistration.DefaultExecutionTimeoutSeconds,
                DeadlineSeconds: 0,
                DeadlineBehavior: DeadlineBehaviorCode.Strict,
                JobRetentionSeconds: JobDefinitionRegistration.DefaultJobRetentionSeconds,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.None,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: delivery,
                AlertChannelName: null,
                RunbookUrl: null
            )
        );

    private sealed class HandlerA;

    private sealed class HandlerB;

    private static JobDescriptor Descriptor(string name, Type handlerType) =>
        new(
            JobName: name,
            HandlerType: handlerType,
            MethodName: "Run",
            InputType: typeof(object),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: static async (_, _, _, _) =>
            {
                await Task.CompletedTask;
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new object(),
            SerializeOutput: null
        );
}
