using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for the operator policy-override write: <c>set_definition_overrides</c> writes only
/// the <c>*_override</c> columns (the DB recomputes the <c>*_effective</c> generated columns), leaves the
/// code defaults and <c>definition_hash</c> untouched, is version-guarded, and emits a definition-scoped
/// <c>definition.overrides-updated</c> event.
/// </summary>
[ConformanceSpec(
    "set-definition-overrides.write",
    "Override writes are version-guarded, recompute effective, and audited",
    Area = "Catalog",
    Contract = "Applies an override set version-guarded, recomputes effective, leaves defaults and definition_hash untouched, and emits a policy-changed event."
)]
public sealed class SetDefinitionOverridesSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Actor = "tester";

    [Fact(DisplayName = "Competing definition overrides with one version have one winner and one audit event")]
    public async Task Concurrent_overrides_have_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("override-race");
        var id = await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable
            .Range(0, 8)
            .Select(async i =>
            {
                await start.Task;
                return await DefinitionTestOps.UpdateOverridesAsync(
                    Services,
                    TestNamespace,
                    name,
                    before.Version,
                    new DefinitionPolicyOverrides(MaxAttempts: 10 + i),
                    Actor,
                    "race",
                    ct
                );
            })
            .ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(before.Version + 1, Assert.Single(outcomes, o => o.Action == ControlAction.Applied).Version);
        Assert.Equal(7, outcomes.Count(o => o.Action == ControlAction.VersionConflict));
        Assert.Equal(before.Version + 1, (await ReadAsync(name, ct)).Version);
        var events = await Db.From<EventEntity>()
            .Where(e => e.DefinitionId == id && e.EventCode == EventCode.JobDefinitionOverridesUpdated)
            .ToListAsync(ct);
        Assert.Single(events);
    }

    private static JobDescriptor Def(string name, int maxAttempts) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
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

    private async Task<int> RegisterAsync(string name, int maxAttempts, CancellationToken ct)
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
        var name = Unique("set-ovr");
        await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            before.Version,
            new DefinitionPolicyOverrides(MaxAttempts: 9),
            Actor,
            "bump retries",
            ct
        );

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var after = await ReadAsync(name, ct);
        Assert.Equal(3, after.MaxAttempts); // default untouched
        Assert.Equal(9, after.MaxAttemptsOverride);
        Assert.Equal(9, after.MaxAttemptsEffective); // DB recomputed COALESCE
        Assert.Equal(before.DefinitionHash, after.DefinitionHash); // hash untouched
        Assert.True(after.Version > before.Version);
        Assert.Equal(after.Version, outcome.Version);
    }

    [Fact(DisplayName = "Clearing an override reverts effective to the default")]
    public async Task Clear_override_reverts_effective()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("clear-ovr");
        await RegisterAsync(name, 4, ct);

        var v0 = (await ReadAsync(name, ct)).Version;
        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            v0,
            new DefinitionPolicyOverrides(MaxAttempts: 12),
            Actor,
            "set",
            ct
        );
        var set = await ReadAsync(name, ct);
        Assert.Equal(12, set.MaxAttemptsEffective);

        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            set.Version,
            new DefinitionPolicyOverrides(),
            Actor,
            "clear",
            ct
        );
        var cleared = await ReadAsync(name, ct);
        Assert.Null(cleared.MaxAttemptsOverride);
        Assert.Equal(4, cleared.MaxAttemptsEffective); // reverts to default
    }

    [Fact(DisplayName = "A stale version is VersionConflict with the current version and changes nothing")]
    public async Task Stale_version_is_a_version_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = Unique("stale");
        await RegisterAsync(name, 5, ct);
        var before = await ReadAsync(name, ct);

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            before.Version + 1,
            new DefinitionPolicyOverrides(MaxAttempts: 99),
            Actor,
            "stale",
            ct
        );

        Assert.Equal(ControlAction.VersionConflict, outcome.Action);
        Assert.Equal(before.Version, outcome.Version);
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
        var name = Unique("bad-backoff");
        await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DefinitionTestOps.UpdateOverridesAsync(
                Services,
                TestNamespace,
                name,
                before.Version,
                new DefinitionPolicyOverrides(Backoff: badBackoff),
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
    [InlineData(0, null, null, null)]
    [InlineData(-1, null, null, null)]
    [InlineData(null, 0, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, null, -1, null)]
    [InlineData(null, null, null, -1)]
    public async Task Out_of_range_override_is_rejected(
        int? maxAttempts,
        int? executionTimeoutSeconds,
        int? deadlineSeconds,
        int? jobRetentionSeconds
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("out-of-range");
        await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Services
                .GetRequiredService<IDefinitions>()
                .UpdateOverridesAsync(
                    TestNamespace,
                    name,
                    before.Version,
                    new DefinitionPolicyOverrides(
                        MaxAttempts: maxAttempts,
                        ExecutionTimeoutSeconds: executionTimeoutSeconds,
                        DeadlineSeconds: deadlineSeconds,
                        JobRetentionSeconds: jobRetentionSeconds
                    ),
                    "out of range",
                    Actor,
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
        var name = Unique("boundary-ovr");
        await RegisterAsync(name, 3, ct);
        var before = await ReadAsync(name, ct);

        var result = await Services
            .GetRequiredService<IDefinitions>()
            .UpdateOverridesAsync(
                TestNamespace,
                name,
                before.Version,
                new DefinitionPolicyOverrides(MaxAttempts: 1, DeadlineSeconds: 0, JobRetentionSeconds: 0),
                "boundary",
                Actor,
                ct
            );

        Assert.Equal(ControlAction.Applied, result.Action);
    }

    [Fact(DisplayName = "An unknown definition name is NotFound")]
    public async Task Unknown_name_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            Unique("ghost-definition"),
            0,
            new DefinitionPolicyOverrides(MaxAttempts: 1),
            Actor,
            "ghost",
            ct
        );

        Assert.Equal(ControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "A definition-scoped policy-changed event is emitted")]
    public async Task Emits_definition_policy_changed_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = Store();
        var name = Unique("audit");
        var id = await RegisterAsync(name, 6, ct);
        var v0 = (await ReadAsync(name, ct)).Version;

        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            v0,
            new DefinitionPolicyOverrides(MaxAttempts: 7),
            Actor,
            "audited edit",
            ct
        );

        var evt = await Db.From<EventEntity>()
            .Where(e => e.DefinitionId == id && e.EventCode == EventCode.JobDefinitionOverridesUpdated)
            .SingleOrDefaultAsync(ct);

        Assert.NotNull(evt);
        Assert.Equal(EntityCode.Definition, evt!.EntityCode);
        Assert.Equal(id, evt.EntityId);
        Assert.Equal("tester", EventDetail(evt, "actor"));
        Assert.Equal("audited edit", evt.EventMessage);
    }
}
