using System.Text;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Settings;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Settings;

/// <summary>Conformance for the scoped settings roundtrip: set creates then overwrites with a version bump and a setting.updated event naming the setting, scopes address distinct rows, unregistered targets are NotFound, and get is exact-scope.</summary>
[ConformanceSpec(
    "settings.set-get",
    "A setting is set and read back by name at its inferred scope",
    Area = "Admin",
    Contract = "Set upserts one setting at the scope inferred from its targets with a version bump and emits setting.updated naming the setting.",
    Arrange = "A unique setting name, the test namespace, and one registered definition.",
    Act = "The setting is set and read at global, namespace, and definition scope, twice at one scope, and against an unknown namespace.",
    Assert = "Scopes address distinct rows, rewrites bump the version, unknown targets are NotFound, and each set emits its event."
)]
[CoversStoreMethod(typeof(ISettingStore), nameof(ISettingStore.GetSettingAsync))]
[CoversStoreMethod(typeof(ISettingStore), nameof(ISettingStore.SetSettingAsync))]
public abstract class SetSettingSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private SettingsService Service => Services.GetRequiredService<SettingsService>();

    [Fact(DisplayName = "Set creates then overwrites with a version bump; get returns the latest value")]
    public async Task Set_roundtrips_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("cfg");

        Assert.Null(await Service.GetAsync(name, namespaceName: null, jobName: null, ct));

        var created = await Service.SetAsync(name, "42", "the answer", namespaceName: null, jobName: null, null, "op-1", ct);
        Assert.Equal(AdminControlAction.Applied, created.Action);
        Assert.Equal(0, created.Version);

        var first = await Service.GetAsync(name, namespaceName: null, jobName: null, ct);
        Assert.NotNull(first);
        Assert.Equal("42", first.Value);
        Assert.Equal("the answer", first.Description);
        Assert.Equal(0, first.Version);

        var overwritten = await Service.SetAsync(name, "43", "the answer", namespaceName: null, jobName: null, null, "op-1", ct);
        Assert.Equal(AdminControlAction.Applied, overwritten.Action);
        Assert.Equal(1, overwritten.Version);

        var second = await Service.GetAsync(name, namespaceName: null, jobName: null, ct);
        Assert.Equal("43", second!.Value);
        Assert.Equal(1, second.Version);
    }

    [Fact(DisplayName = "One name addresses distinct rows at global, namespace, and definition scope")]
    public async Task Scopes_address_distinct_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("cfg-scope");
        var jobName = TestKey("cfg-job");
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen, [Def(jobName)], ct);

        await Service.SetAsync(name, "global", null, namespaceName: null, jobName: null, null, "op-1", ct);
        await Service.SetAsync(name, "ns", null, TestNamespace, jobName: null, null, "op-1", ct);
        await Service.SetAsync(name, "def", null, TestNamespace, jobName, null, "op-1", ct);

        Assert.Equal("global", (await Service.GetAsync(name, null, null, ct))!.Value);
        Assert.Equal("ns", (await Service.GetAsync(name, TestNamespace, null, ct))!.Value);
        Assert.Equal("def", (await Service.GetAsync(name, TestNamespace, jobName, ct))!.Value);
    }

    [Fact(DisplayName = "An unregistered namespace or definition target is NotFound and writes nothing")]
    public async Task Unregistered_targets_are_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("cfg-miss");

        var unknownNamespace = await Service.SetAsync(name, "x", null, TestKey("no-such-ns"), jobName: null, null, "op-1", ct);
        Assert.Equal(AdminControlAction.NotFound, unknownNamespace.Action);

        var unknownDefinition = await Service.SetAsync(name, "x", null, TestNamespace, TestKey("no-such-job"), null, "op-1", ct);
        Assert.Equal(AdminControlAction.NotFound, unknownDefinition.Action);

        Assert.Null(await Service.GetAsync(name, TestNamespace, null, ct));
        Assert.Empty(await Db.From<Setting>().Where(s => s.Name == name).ToListAsync(ct));
    }

    [Fact(DisplayName = "Every set emits setting.updated whose detail carries the setting name")]
    public async Task Set_emits_the_evidence_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("cfg-ev");
        var reason = TestKey("because");

        await Service.SetAsync(name, "on", null, TestNamespace, jobName: null, reason, "op-1", ct);

        var events = await Db.From<JobEvent>()
            .Where(e => e.EventCode == EventCode.SettingUpdated && e.ReasonMessage == reason)
            .ToListAsync(ct);
        var evt = Assert.Single(events);
        Assert.Equal(TestNamespaceId, evt.NamespaceId);
        Assert.Equal($"{{\"name\":\"{name}\"}}", Encoding.UTF8.GetString(evt.Detail!));
    }

    // Framework defaults fill the policy columns; only the identity matters to these facts.
    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 3,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
