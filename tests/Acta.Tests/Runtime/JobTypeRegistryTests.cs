using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// JobTypeRegistry contract: routes build from catalogs (references and workers alike), duplicate
/// (namespace, jobName) routes for one type dedupe, and the unregistered-type error names both
/// Reference and Run.
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

    private static CatalogDeclaration Catalog(string ns, params JobDescriptor[] descriptors) =>
        new(ns, [new ManifestRegistration(typeof(object), () => new JobDescriptorManifest([.. descriptors]))]);

    private static JobDescriptor Descriptor(string jobName, Type inputType) =>
        new(
            JobName: jobName,
            HandlerType: typeof(object),
            MethodName: "Handle",
            InputType: inputType,
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
