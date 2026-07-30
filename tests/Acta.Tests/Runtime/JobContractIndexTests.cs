using Acta.Modules.Execution.Definitions;
using Acta.Payloads;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class JobContractIndexTests
{
    private sealed record PingInput(int N);

    private sealed class ManifestA : IJobManifest
    {
        public static JobDescriptorManifest Descriptors => new([]);
    }

    private sealed class ManifestB : IJobManifest
    {
        public static JobDescriptorManifest Descriptors => new([]);
    }

    [Fact]
    public void Resolves_route_by_manifest_type_and_job_name()
    {
        var index = JobContractIndex.Build([Catalog("payments", typeof(ManifestA), Descriptor("ping", typeof(PingInput)))]);

        var route = index.Resolve(typeof(ManifestA), "ping", namespaceHint: null);

        Assert.Equal("payments", route.Namespace);
        Assert.Equal("ping", route.JobName);
        Assert.Equal(JobPayloadFormat.Json.Id, route.InputFormat.Id);
        Assert.Equal(typeof(PingInput), route.InputType);
        Assert.Null(route.OutputType);
    }

    [Fact]
    public void Unregistered_manifest_throws_naming_the_type()
    {
        var index = JobContractIndex.Build([]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(ManifestA), "ping", null));

        Assert.Contains(typeof(ManifestA).FullName!, ex.Message);
    }

    [Fact]
    public void Multiple_namespaces_without_hint_throws_listing_candidates()
    {
        var index = JobContractIndex.Build([
            Catalog("eu", typeof(ManifestA), Descriptor("ping", typeof(PingInput))),
            Catalog("us", typeof(ManifestA), Descriptor("ping", typeof(PingInput))),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(ManifestA), "ping", null));
        Assert.Contains("eu", ex.Message);
        Assert.Contains("us", ex.Message);

        var route = index.Resolve(typeof(ManifestA), "ping", namespaceHint: "us");
        Assert.Equal("us", route.Namespace);
    }

    [Fact]
    public void Non_matching_namespace_hint_throws()
    {
        var index = JobContractIndex.Build([Catalog("eu", typeof(ManifestA), Descriptor("ping", typeof(PingInput)))]);

        var ex = Assert.Throws<InvalidOperationException>(() => index.Resolve(typeof(ManifestA), "ping", namespaceHint: "us"));
        Assert.Contains("eu", ex.Message);
    }

    [Fact]
    public void Default_contract_has_null_manifest_and_blank_name()
    {
        var def = default(JobContract<PingInput>);
        Assert.Null(def.ManifestType);
        Assert.True(string.IsNullOrWhiteSpace(def.JobName));
    }

    private static JobCatalogRegistration Catalog(string ns, Type manifestType, params JobDescriptor[] descriptors) =>
        new(ns, [new ManifestRegistration(manifestType, () => new JobDescriptorManifest([.. descriptors]))]);

    private static JobDescriptor Descriptor(string jobName, Type inputType) =>
        new(
            JobName: jobName,
            HandlerType: typeof(object),
            MethodName: "Handle",
            InputType: inputType,
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
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
}
