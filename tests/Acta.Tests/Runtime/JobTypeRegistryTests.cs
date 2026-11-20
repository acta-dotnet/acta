using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// JobTypeRegistry contract: routes build from catalogs (references and workers alike), duplicate
/// (namespace, jobName) routes for one type dedupe, the unregistered-type error names both
/// Reference and Run, and an ambiguous type's error leads with the generated call.
/// </summary>
public sealed class JobTypeRegistryTests
{
    private sealed record PingInput(int N);

    [Fact]
    public void Build_indexes_routes_from_a_referenced_catalog()
    {
        var index = JobTypeRegistry.Build([Catalog("payments", Descriptor("ping", typeof(PingInput)))]);

        var route = index.Resolve(typeof(PingInput), namespaceHint: null);

        Assert.Equal("payments", route.Namespace);
        Assert.Equal("ping", route.JobName);
    }

    [Fact]
    public void Build_dedupes_the_same_route_from_reference_and_run()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput))),
            Catalog("payments", Descriptor("ping", typeof(PingInput))),
        ]);

        var route = index.Resolve(typeof(PingInput), namespaceHint: null);
        Assert.Equal("payments", route.Namespace);
    }

    [Fact]
    public void Unregistered_type_error_mentions_Reference_and_Run()
    {
        var index = JobTypeRegistry.Build([]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(PingInput), null));

        Assert.Contains("j.Reference<TManifest>", ex.Message);
        Assert.Contains("j.Run<TManifest>", ex.Message);
    }

    // A namespace option cannot tell apart two jobs of one namespace, so the advice leaves it out.
    [Fact]
    public void Two_jobs_in_one_namespace_advise_the_generated_call_and_not_the_namespace()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput)), Descriptor("pong", typeof(PingInput))),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(PingInput), null));

        Assert.Equal(
            $"Input type '{typeof(PingInput).FullName}' maps to multiple jobs in namespace 'payments' (ping, pong). Enqueue with "
                + "the job's generated call, which names the job, or use the raw EnqueueJobRequest path.",
            ex.Message
        );
    }

    [Fact]
    public void Two_jobs_in_two_namespaces_advise_the_generated_call_then_the_namespace_then_the_raw_path()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput))),
            Catalog("billing", Descriptor("pong", typeof(PingInput))),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(PingInput), null));

        Assert.Equal(
            $"Input type '{typeof(PingInput).FullName}' maps to multiple jobs (payments/ping, billing/pong). Enqueue with the "
                + "job's generated call, which names the job. Otherwise set JobEnqueueOptions.JobNamespace to pick one "
                + "namespace, or use the raw EnqueueJobRequest path.",
            ex.Message
        );
    }

    [Fact]
    public void One_job_in_two_namespaces_advises_the_namespace_since_the_generated_call_alone_cannot_pick()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput))),
            Catalog("billing", Descriptor("ping", typeof(PingInput))),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(PingInput), null));

        Assert.Equal(
            $"Input type '{typeof(PingInput).FullName}' maps to multiple jobs (payments/ping, billing/ping). Set "
                + "JobEnqueueOptions.JobNamespace to pick one namespace, with the input or with the job's generated call, or "
                + "use the raw EnqueueJobRequest path.",
            ex.Message
        );
    }

    [Fact]
    public void A_namespace_hint_picks_the_one_job_of_that_namespace()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput))),
            Catalog("billing", Descriptor("pong", typeof(PingInput))),
        ]);

        var route = index.Resolve(typeof(PingInput), namespaceHint: "billing");

        Assert.Equal(("billing", "pong"), (route.Namespace, route.JobName));
    }

    [Fact]
    public void A_namespace_hint_naming_a_namespace_with_two_jobs_answers_the_within_namespace_advice()
    {
        var index = JobTypeRegistry.Build([
            Catalog("payments", Descriptor("ping", typeof(PingInput)), Descriptor("pong", typeof(PingInput))),
            Catalog("billing", Descriptor("ping", typeof(PingInput))),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(PingInput), "payments"));

        Assert.Equal(
            $"Input type '{typeof(PingInput).FullName}' maps to multiple jobs in namespace 'payments' (ping, pong). Enqueue with "
                + "the job's generated call, which names the job, or use the raw EnqueueJobRequest path.",
            ex.Message
        );
    }

    [Fact]
    public void Several_no_input_jobs_in_one_namespace_are_named_as_sharing_NoInput()
    {
        var index = JobTypeRegistry.Build([Catalog("payments", Descriptor("sweep", typeof(NoInput)), Descriptor("reap", typeof(NoInput)))]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(NoInput), null));

        Assert.Equal(
            "Input type 'Acta.NoInput' maps to multiple jobs in namespace 'payments' (sweep, reap): several no-input jobs share "
                + "NoInput. Enqueue with the job's generated call, which names the job, or use the raw EnqueueJobRequest path.",
            ex.Message
        );
    }

    private static CatalogDeclaration Catalog(string ns, params JobDescriptor[] descriptors) =>
        new(ns, [new ManifestRegistration(typeof(object), () => new JobDescriptorManifest([.. descriptors]))]);

    private static JobDescriptor Descriptor(string jobName, Type inputType) =>
        new(
            JobName: jobName,
            HandlerType: typeof(object),
            MethodName: "Handle",
            InputType: inputType,
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Bulk,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );
}
