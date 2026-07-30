using System.Collections.Immutable;
using Acta.Modules.Execution.Workers;
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

        var ex = Assert.Throws<InvalidOperationException>(() => WorkerRuntimeInitializer.ValidateUniqueJobNames(descriptors));

        Assert.Contains("send-receipt", ex.Message);
        Assert.Contains(typeof(HandlerA).FullName!, ex.Message);
        Assert.Contains(typeof(HandlerB).FullName!, ex.Message);
        Assert.DoesNotContain("unrelated", ex.Message);
    }

    [Fact]
    public void Unique_job_names_pass()
    {
        var descriptors = ImmutableArray.Create(Descriptor("job-one", typeof(HandlerA)), Descriptor("job-two", typeof(HandlerB)));

        WorkerRuntimeInitializer.ValidateUniqueJobNames(descriptors);
    }

    [Fact]
    public void A_claiming_worker_with_zero_descriptors_fails_startup()
    {
        // A module-less worker with framework jobs disabled would claim namespace jobs it can never
        // dispatch; the claimed rows would rot until lease recovery.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkerRuntimeInitializer.ValidateHasDescriptors("billing", ImmutableArray<JobDescriptor>.Empty)
        );

        Assert.Contains("billing", ex.Message);
    }

    [Fact]
    public void A_worker_with_at_least_one_descriptor_passes()
    {
        WorkerRuntimeInitializer.ValidateHasDescriptors("billing", ImmutableArray.Create(Descriptor("job-one", typeof(HandlerA))));
    }

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
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: JobAlertProfileCode.None,
            Invoker: static async (_, _, _, _) =>
            {
                await Task.CompletedTask;
                return new JobHandlerInvocationResult(false, null);
            },
            DeserializeInput: static (_, _) => new object(),
            SerializeOutput: null
        );
}
