using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for the operator policy-override write: <c>set_job_definition_overrides</c> writes only
/// the <c>*_override</c> columns (the DB recomputes the <c>*_effective</c> generated columns), leaves the
/// code defaults and <c>definition_hash</c> untouched, is version-guarded, and emits a definition-scoped
/// <c>definition.overrides-updated</c> event.
/// </summary>
[ConformanceSpec(
    "set-definition-overrides.write",
    "Override writes are version-guarded, recompute effective, and audited",
    Area = "Catalog",
    Contract = "Applies an override set version-guarded, recomputes effective, leaves defaults and definition_hash untouched, and emits a policy-changed event.",
    Arrange = "A registered definition carries code-default policy columns.",
    Act = "An override set is applied then cleared, and stale-version and unknown-id writes are attempted.",
    Assert = "Only the override columns change with effective recomputed, defaults and definition_hash stay put, bad writes reject, and a policy-changed event lands."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.SetDefinitionOverridesAsync))]
public abstract class SetJobDefinitionOverridesSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static JobControlActor Actor => new(JobActorCode.Operator, "tester");

    private static JobDescriptor Def(string name, short maxAttempts) =>
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

    private async Task<int> RegisterAsync(string name, short maxAttempts, CancellationToken ct)
    {
        var (_, _) = Store();
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen, [Def(name, maxAttempts)], ct);
        return (await ReadAsync(name, ct)).Id;
    }

    [Fact(DisplayName = "Setting an override recomputes effective and leaves the default + hash untouched")]
    public async Task Set_override_recomputes_effective()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("set-ovr");
        var id = await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            id,
            before.Version,
            new JobDefinitionPolicyOverrides(MaxAttempts: 9),
            Actor,
            "bump retries",
            ct
        );

        Assert.Equal(JobControlAction.Applied, outcome.Action);
        var after = await ReadAsync(name, ct);
        Assert.Equal((short)3, after.MaxAttempts); // default untouched
        Assert.Equal((short)9, after.MaxAttemptsOverride);
        Assert.Equal((short)9, after.MaxAttemptsEffective); // DB recomputed COALESCE
        Assert.Equal(before.DefinitionHash, after.DefinitionHash); // hash untouched
        Assert.True(after.Version > before.Version);
    }

    [Fact(DisplayName = "Clearing an override reverts effective to the default")]
    public async Task Clear_override_reverts_effective()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("clear-ovr");
        var id = await RegisterAsync(name, 4, ct);

        var v0 = (await ReadAsync(name, ct)).Version;
        await DefinitionTestOps.UpdateOverridesAsync(Services, id, v0, new JobDefinitionPolicyOverrides(MaxAttempts: 12), Actor, "set", ct);
        var set = await ReadAsync(name, ct);
        Assert.Equal((short)12, set.MaxAttemptsEffective);

        await DefinitionTestOps.UpdateOverridesAsync(Services, id, set.Version, new JobDefinitionPolicyOverrides(), Actor, "clear", ct);
        var cleared = await ReadAsync(name, ct);
        Assert.Null(cleared.MaxAttemptsOverride);
        Assert.Equal((short)4, cleared.MaxAttemptsEffective); // reverts to default
    }

    [Fact(DisplayName = "A stale version is rejected and changes nothing")]
    public async Task Stale_version_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("stale");
        var id = await RegisterAsync(name, 5, ct);
        var before = await ReadAsync(name, ct);

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            id,
            before.Version + 1,
            new JobDefinitionPolicyOverrides(MaxAttempts: 99),
            Actor,
            "stale",
            ct
        );

        Assert.Equal(JobControlAction.Rejected, outcome.Action);
        var after = await ReadAsync(name, ct);
        Assert.Null(after.MaxAttemptsOverride);
        Assert.Equal(before.Version, after.Version);
    }

    [Theory(DisplayName = "An invalid or over-long backoff override is rejected and writes nothing")]
    [InlineData("not a backoff")]
    [InlineData("1s..2s exact exact exact exact exact exact exact exact exact exact exact")] // 74 chars, valid DSL but > 64
    public async Task Invalid_backoff_override_is_rejected(string badBackoff)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = Store();
        var name = TestKey("bad-backoff");
        var id = await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DefinitionTestOps.UpdateOverridesAsync(
                Services,
                id,
                before.Version,
                new JobDefinitionPolicyOverrides(Backoff: badBackoff),
                Actor,
                "bad",
                ct
            )
        );

        var after = await ReadAsync(name, ct);
        Assert.Null(after.BackoffOverride);
        Assert.Equal(before.Version, after.Version);
    }

    [Theory(DisplayName = "An out-of-range numeric override is rejected before any write, through the guarded API")]
    [InlineData((short)0, null, null, null)]
    [InlineData((short)-1, null, null, null)]
    [InlineData(null, 0, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, null, -1, null)]
    [InlineData(null, null, null, -1)]
    public async Task Out_of_range_override_is_rejected(
        short? maxAttempts,
        int? executionTimeoutSeconds,
        int? deadlineSeconds,
        int? jobRetentionSeconds
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("out-of-range");
        var id = await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DefinitionsApi(Services.GetRequiredService<DefinitionsService>())
                .UpdateOverridesAsync(
                    id,
                    before.Version,
                    new JobDefinitionPolicyOverrides(
                        MaxAttempts: maxAttempts,
                        ExecutionTimeoutSeconds: executionTimeoutSeconds,
                        DeadlineSeconds: deadlineSeconds,
                        JobRetentionSeconds: jobRetentionSeconds
                    ),
                    Actor.ActorKey,
                    "out of range",
                    ct
                )
                .AsTask()
        );

        var after = await ReadAsync(name, ct);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact(DisplayName = "Boundary override values (MaxAttempts 1, DeadlineSeconds 0, JobRetentionSeconds 0) are applied")]
    public async Task Boundary_override_values_are_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("boundary-ovr");
        var id = await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        var result = await new DefinitionsApi(Services.GetRequiredService<DefinitionsService>()).UpdateOverridesAsync(
            id,
            before.Version,
            new JobDefinitionPolicyOverrides(MaxAttempts: 1, DeadlineSeconds: 0, JobRetentionSeconds: 0),
            Actor.ActorKey,
            "boundary",
            ct
        );

        Assert.Equal(JobControlAction.Applied, result.Action);
    }

    [Fact(DisplayName = "An unknown definition id is NotFound")]
    public async Task Unknown_id_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            int.MaxValue,
            0,
            new JobDefinitionPolicyOverrides(MaxAttempts: 1),
            Actor,
            "ghost",
            ct
        );

        Assert.Equal(JobControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "A definition-scoped policy-changed event is emitted")]
    public async Task Emits_definition_policy_changed_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = Store();
        var name = TestKey("audit");
        var id = await RegisterAsync(name, 6, ct);
        var v0 = (await ReadAsync(name, ct)).Version;

        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            id,
            v0,
            new JobDefinitionPolicyOverrides(MaxAttempts: 7),
            Actor,
            "audited edit",
            ct
        );

        var evt = await Db.From<JobEvent>()
            .Where(e => e.DefinitionId == id && e.EventCode == JobEventCode.JobDefinitionOverridesUpdated)
            .SingleOrDefaultAsync(ct);

        Assert.NotNull(evt);
        Assert.Null(evt!.JobId); // definition-scoped, not a job instance
        Assert.Equal(JobActorCode.Operator, evt.ActorCode);
        Assert.Equal("tester", evt.ActorKey);
        Assert.Equal("audited edit", evt.ReasonMessage);
    }
}
