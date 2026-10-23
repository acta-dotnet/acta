using System.Collections.Immutable;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for monotonic job-definition promotion: newer-or-equal generations may update policy
/// and contract; older generations are read-only and cannot reactivate or rewrite newer rows.
/// </summary>
[ConformanceSpec(
    "register-definitions.monotonic-promotion",
    "Newer-or-equal generation promotes policy; older cannot downgrade",
    Area = "Catalog",
    Contract = "Writes a definition only when the incoming manifest generation is at or above the stored one, and never touches a definition the manifest omits."
)]
public sealed class RegisterDefinitionsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Gen1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Gen2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Gen3 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JobDescriptor Def(string name, int maxAttempts, Type inputType) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: inputType,
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: maxAttempts,
            EventRecording: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    private async Task<DefinitionEntity> ReadAsync(string name, CancellationToken ct)
    {
        var def = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == TestNamespaceId && d.DefinitionName == name)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        return def!;
    }

    [Fact(DisplayName = "Newer generation updates policy and bumps version")]
    public async Task Newer_generation_updates_policy_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("promote");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 9, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(9, second.MaxAttempts);
        Assert.Equal(Gen2, second.ManifestGenerationAtUtc);
        Assert.True(second.Version > first.Version);
    }

    [Fact(DisplayName = "Older generation does not change policy or version")]
    public async Task Older_generation_does_not_change_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("no-downgrade");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 9, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(9, second.MaxAttempts);
        Assert.Equal(Gen2, second.ManifestGenerationAtUtc);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "Equal generation with a real difference is applied")]
    public async Task Equal_generation_applies_a_real_difference()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("equal-correction");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 5, typeof(int))], ct);

        var def = await ReadAsync(name, ct);
        Assert.Equal(5, def.MaxAttempts);
    }

    [Fact(DisplayName = "Unchanged restart writes nothing")]
    public async Task Unchanged_restart_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("idempotent");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "A newer unchanged build stores only its generation, so an older build registering after it changes nothing")]
    public async Task A_newer_unchanged_build_stores_its_generation_so_an_older_build_after_it_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("three-builds");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(name, 3, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen3, [Def(name, 3, typeof(int))], ct);
        var newest = await ReadAsync(name, ct);
        Assert.Equal(Gen3, newest.ManifestGenerationAtUtc);
        Assert.Equal(first.DefinitionHash, newest.DefinitionHash);
        Assert.Equal(first.Version, newest.Version);
        Assert.Equal(first.ModifiedAtUtc, newest.ModifiedAtUtc);

        // The build between the two declares another policy and registers last.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 9, typeof(int))], ct);
        var after = await ReadAsync(name, ct);
        Assert.Equal(3, after.MaxAttempts);
        Assert.Equal(first.DefinitionHash, after.DefinitionHash);
        Assert.Equal(Gen3, after.ManifestGenerationAtUtc);
        Assert.Equal(first.Version, after.Version);
    }

    [Fact(DisplayName = "A definition absent from the manifest stays Active with its jobs")]
    public async Task A_definition_absent_from_the_manifest_stays_active_with_its_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = Unique("absent-keep");
        var absent = Unique("absent-gone");

        await DefinitionTestOps.RegisterAsync(
            Services,
            TestNamespaceId,
            Gen1,
            [Def(keep, 1, typeof(int)), Def(absent, 1, typeof(int))],
            ct
        );

        var jobs = Services.GetRequiredService<IJobs>();
        var payload = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Json.Id).Serialize(1);
        var parked = await jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, absent, payload, null, null, null), ct);
        var before = await ReadAsync(absent, ct);

        // Re-register without 'absent'. Changing 'keep' defeats the C#-side no-op write gate, so the
        // routine really runs against a manifest that omits a stored definition.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(keep, 2, typeof(int))], ct);

        var after = await ReadAsync(absent, ct);
        Assert.Equal(DefinitionStatusCode.Active, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(JobStatusCode.Ready, (await jobs.GetStatusAsync(parked, ct))?.Status);
    }

    [Fact(DisplayName = "Older generation cannot reactivate or rewrite a newer retired definition")]
    public async Task Older_worker_cannot_reactivate_or_rewrite_a_newer_retired_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var job = Unique("react-job");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(job, 1, typeof(int))], ct);
        await RetireAsync(job, ct);
        var retired = await ReadAsync(job, ct);
        Assert.Equal(DefinitionStatusCode.Retired, retired.Status);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(job, 7, typeof(int))], ct);
        var still = await ReadAsync(job, ct);
        Assert.Equal(DefinitionStatusCode.Retired, still.Status);
        Assert.Equal(retired.Version, still.Version);
    }

    // Retirement is an operator verb, never something registration does, so the Retired row this
    // generation gate has to hold against is arranged through that verb.
    private async Task RetireAsync(string name, CancellationToken ct)
    {
        var definition = await ReadAsync(name, ct);
        var outcome = await Services
            .GetRequiredService<IDefinitions>()
            .RetireAsync(TestNamespace, name, definition.Version, actor: "tester", ct: ct);
        Assert.Equal(ControlAction.Applied, outcome.Action);
    }

    [Fact(DisplayName = "Fail-mode contract drift blocks before any registration write")]
    public async Task Fail_mode_blocks_before_any_registration_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var job = Unique("fail-block");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(job, 1, typeof(int))], ct);
        var before = await ReadAsync(job, ct);

        var stored = (
            await Services
                .GetRequiredService<GetDefinitionContractsHandler>()
                .HandleAsync(new GetDefinitionContractsQuery(TestNamespaceId), ct)
        ).Contracts;
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
