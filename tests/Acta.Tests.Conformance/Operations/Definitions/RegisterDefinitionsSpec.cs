using System.Collections.Immutable;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for job-definition registration: the last registration wins, an unchanged one writes nothing, and a
/// definition the manifest omits is left as it is.
/// </summary>
[ConformanceSpec(
    "register-definitions.writes-what-changed",
    "Registration writes what changed and leaves what the manifest omits",
    Area = "Catalog",
    Contract = "Writes a definition only when it is new, not Active, or changed, and never touches a definition the manifest omits."
)]
public sealed class RegisterDefinitionsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static JobDescriptor Def(string name, int maxAttempts, Type inputType) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: inputType,
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            ResultPayloadFormat: null,
            Priority: default,
            MaxAttempts: maxAttempts,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
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

    [Fact(DisplayName = "A registration with a changed policy updates it and bumps the version")]
    public async Task A_changed_registration_updates_policy_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("promote");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(name, 3, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(name, 9, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(9, second.MaxAttempts);
        Assert.True(second.Version > first.Version);
    }

    [Fact(DisplayName = "A batch that changes one definition leaves an unchanged one at its version")]
    public async Task A_batch_that_changes_one_definition_leaves_an_unchanged_one_as_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var changed = Unique("batch-changed");
        var unchanged = Unique("batch-unchanged");

        await DefinitionTestOps.RegisterAsync(
            Services,
            TestNamespaceId,
            [Def(changed, 3, typeof(int)), Def(unchanged, 3, typeof(int))],
            ct
        );
        var before = await ReadAsync(unchanged, ct);

        // The batch reaches the upsert because one row changed, so the statement itself has to pass over the other.
        var ids = await DefinitionTestOps.RegisterAsync(
            Services,
            TestNamespaceId,
            [Def(changed, 9, typeof(int)), Def(unchanged, 3, typeof(int))],
            ct
        );
        var after = await ReadAsync(unchanged, ct);

        Assert.Equal(9, (await ReadAsync(changed, ct)).MaxAttempts);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ModifiedAtUtc, after.ModifiedAtUtc);
        Assert.Equal(before.Id, ids[unchanged]);
        Assert.Equal(2, ids.Count);
    }

    [Fact(DisplayName = "Unchanged restart writes nothing")]
    public async Task Unchanged_restart_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("idempotent");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(name, 4, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(name, 4, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "A definition absent from the manifest stays Active with its jobs")]
    public async Task A_definition_absent_from_the_manifest_stays_active_with_its_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = Unique("absent-keep");
        var absent = Unique("absent-gone");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(keep, 1, typeof(int)), Def(absent, 1, typeof(int))], ct);

        var jobs = Services.GetRequiredService<IJobs>();
        var payload = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Json.Id).Serialize(1);
        var parked = await jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, absent, payload, null, null, null), ct);
        var before = await ReadAsync(absent, ct);

        // Re-register without 'absent'. Changing 'keep' defeats the C#-side no-op write gate, so the
        // routine really runs against a manifest that omits a stored definition.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(keep, 2, typeof(int))], ct);

        var after = await ReadAsync(absent, ct);
        Assert.Equal(DefinitionStatusCode.Active, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(JobStatusCode.Ready, (await jobs.GetStatusAsync(parked, ct))?.Status);
    }

    [Fact(DisplayName = "Fail-mode contract drift blocks before any registration write")]
    public async Task Fail_mode_blocks_before_any_registration_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var job = Unique("fail-block");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(job, 1, typeof(int))], ct);
        var before = await ReadAsync(job, ct);

        var stored = (
            await Services
                .GetRequiredService<ListDefinitionContractsHandler>()
                .HandleAsync(new ListDefinitionContractsQuery(TestNamespaceId), ct)
        ).Contracts;
        var incoming = ImmutableArray.Create(Def(job, 1, typeof(string)));
        var drifts = ContractDriftDetector.Detect(incoming, stored);

        Assert.Throws<PayloadContractDriftException>(() =>
            ContractDriftPolicy.Apply(PayloadContractDriftMode.Fail, drifts, TestNamespace, NullLogger.Instance)
        );

        var after = await ReadAsync(job, ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.InputTypeName, after.InputTypeName);
    }
}
