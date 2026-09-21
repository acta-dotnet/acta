using System.Collections.Immutable;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for monotonic job-definition promotion: newer-or-equal generations may update policy
/// and contract; older generations are read-only and cannot reactivate or rewrite newer rows.
/// </summary>
[ConformanceSpec(
    "register-definitions.monotonic-promotion",
    "Newer-or-equal generation promotes policy; older cannot downgrade",
    Area = "Catalog",
    Contract = "Writes a definition only when the incoming manifest generation is at or above the stored one, and never touches a definition the manifest omits.",
    Arrange = "A job definition is stored at a known manifest generation.",
    Act = "Definitions are re-registered at newer, equal, and older manifest generations, and with one of them omitted.",
    Assert = "Newer or equal generations update policy, older generations leave the stored row unchanged, and an omitted definition stays Active with its jobs."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.RegisterDefinitionsAsync))]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.GetDefinitionContractsAsync))]
public abstract class MonotonicDefinitionPromotionSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Gen2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JobDescriptor Def(string name, short maxAttempts, Type inputType) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: inputType,
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: maxAttempts,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    private async Task<JobDefinition> ReadAsync(string name, CancellationToken ct)
    {
        var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == TestNamespaceId && d.Name == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        return def!;
    }

    [Fact(DisplayName = "Newer generation updates policy and bumps version")]
    public async Task Newer_generation_updates_policy_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("promote");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 9, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal((short)9, second.MaxAttempts);
        Assert.Equal(Gen2, second.ManifestGenerationAtUtc);
        Assert.True(second.Version > first.Version);
    }

    [Fact(DisplayName = "Older generation does not change policy or version")]
    public async Task Older_generation_does_not_change_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("no-downgrade");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 9, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal((short)9, second.MaxAttempts);
        Assert.Equal(Gen2, second.ManifestGenerationAtUtc);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "Equal generation with a real difference is applied")]
    public async Task Equal_generation_applies_a_real_difference()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("equal-correction");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 5, typeof(int))], ct);

        var def = await ReadAsync(name, ct);
        Assert.Equal((short)5, def.MaxAttempts);
    }

    [Fact(DisplayName = "Unchanged restart writes nothing")]
    public async Task Unchanged_restart_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("idempotent");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "A definition absent from the manifest stays Active with its jobs")]
    public async Task A_definition_absent_from_the_manifest_stays_active_with_its_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = TestKey("absent-keep");
        var absent = TestKey("absent-gone");

        await DefinitionTestOps.RegisterAsync(
            Services,
            TestNamespaceId,
            Gen1,
            [Def(keep, 1, typeof(int)), Def(absent, 1, typeof(int))],
            ct
        );

        var jobs = Services.GetRequiredService<IJobs>();
        var payload = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Json.Id).Serialize(1);
        var parked = await jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, absent, payload, null, null, null), ct);
        var before = await ReadAsync(absent, ct);

        // Re-register without 'absent'. Changing 'keep' defeats the C#-side no-op write gate, so the
        // routine really runs against a manifest that omits a stored definition.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(keep, 2, typeof(int))], ct);

        var after = await ReadAsync(absent, ct);
        Assert.Equal(JobDefinitionStatusCode.Active, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(JobStatusCode.Ready, await jobs.GetStatusAsync(parked, ct));
    }

    [Fact(DisplayName = "Older generation cannot reactivate or rewrite a newer retired definition")]
    public async Task Older_worker_cannot_reactivate_or_rewrite_a_newer_retired_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var job = TestKey("react-job");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(job, 1, typeof(int))], ct);
        await RetireAsync(job, ct);
        var retired = await ReadAsync(job, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, retired.Status);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(job, 7, typeof(int))], ct);
        var still = await ReadAsync(job, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, still.Status);
        Assert.Equal(retired.Version, still.Version);
    }

    // Retirement is an operator verb, never something registration does, so the Retired row this
    // generation gate has to hold against is arranged directly.
    private Task RetireAsync(string name, CancellationToken ct) =>
        Db.From<JobDefinition>()
            .Where(d => d.NamespaceId == TestNamespaceId && d.Name == name)
            .UpdateOnlyAsync(() => new JobDefinition { Status = JobDefinitionStatusCode.Retired }, ct);

    [Fact(DisplayName = "Fail-mode contract drift blocks before any registration write")]
    public async Task Fail_mode_blocks_before_any_registration_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var job = TestKey("fail-block");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(job, 1, typeof(int))], ct);
        var before = await ReadAsync(job, ct);

        var stored = await Services.GetRequiredService<IDefinitionStore>().GetDefinitionContractsAsync(TestNamespaceId, ct);
        var incoming = ImmutableArray.Create(Def(job, 1, typeof(string)));
        var drifts = ContractDriftDetector.Detect(Gen2, incoming, stored);

        Assert.Throws<PayloadContractDriftException>(() =>
            ContractDriftPolicy.Apply(PayloadContractDriftMode.Fail, drifts, TestNamespace, NullLogger.Instance)
        );

        var after = await ReadAsync(job, ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.InputTypeName, after.InputTypeName);
    }
}
