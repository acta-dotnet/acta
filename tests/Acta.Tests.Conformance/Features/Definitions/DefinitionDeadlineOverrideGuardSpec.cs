using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Pins the override-path mirror of the registration-time deadline rule: a deadline anchors to job
/// creation and a recurring slot's row lives forever, so a DeadlineSeconds override on a scheduled
/// definition is rejected instead of landing as a silent no-op. The guard stays narrow: other
/// overrides on a scheduled definition and deadline overrides on unscheduled definitions apply.
/// </summary>
[ConformanceSpec(
    "catalog.deadline-override-guard",
    "A deadline override is rejected on a scheduled definition",
    Area = "Catalog",
    Contract = "A DeadlineSeconds override on a definition whose slot job carries schedule rows is rejected before the write and never lands as a silent no-op.",
    Arrange = "One definition owns a registered recurring slot with a schedule row and a second definition has no schedules.",
    Act = "UpdateOverrides attempts a DeadlineSeconds override on each, plus a deadline-free override on the scheduled one.",
    Assert = "The scheduled definition rejects the deadline override with nothing landed, while the deadline-free and unscheduled-definition overrides both apply."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.DefinitionHasSchedulesAsync))]
public abstract class DefinitionDeadlineOverrideGuardSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private static JobControlActor Actor => new(ActorCode.Operator, "tester");

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

    private async Task<JobDefinition> ReadAsync(string name, CancellationToken ct)
    {
        var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == TestNamespaceId && d.Name == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        return def!;
    }

    private async Task<int> RegisterAsync(string name, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen, [Def(name)], ct);
        return map[name];
    }

    private async Task RegisterScheduleAsync(int defId, string jobName, CancellationToken ct)
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            JobName: jobName,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            AuditLevel: JobAuditLevelCode.Audit,
            SlotStatus: JobStatusCode.Ready,
            SlotMinNextRunAtUtc: Gen,
            Schedules: [new SlotSchedule("only", Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, Gen)]
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    [Fact(DisplayName = "A DeadlineSeconds override on a scheduled definition is rejected and nothing lands")]
    public async Task A_deadline_override_on_a_scheduled_definition_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("dl-guard");
        var defId = await RegisterAsync(name, ct);
        await RegisterScheduleAsync(defId, name, ct);

        var before = await ReadAsync(name, ct);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DefinitionTestOps.UpdateOverridesAsync(
                Services,
                TestNamespace,
                name,
                before.Version,
                new JobDefinitionPolicyOverrides(DeadlineSeconds: 444),
                Actor,
                "attempt deadline",
                ct
            )
        );

        var after = await ReadAsync(name, ct);
        Assert.Null(after.DeadlineSecondsOverride);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact(DisplayName = "A deadline-free override still applies to a scheduled definition")]
    public async Task A_deadline_free_override_applies_to_a_scheduled_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("dl-free");
        var defId = await RegisterAsync(name, ct);
        await RegisterScheduleAsync(defId, name, ct);

        var before = await ReadAsync(name, ct);
        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            before.Version,
            new JobDefinitionPolicyOverrides(MaxAttempts: 7),
            Actor,
            "raise attempts",
            ct
        );

        Assert.Equal(ControlAction.Applied, outcome.Action);
        Assert.Equal((short)7, (await ReadAsync(name, ct)).MaxAttemptsOverride);
    }

    [Fact(DisplayName = "A DeadlineSeconds override on an unscheduled definition applies")]
    public async Task A_deadline_override_on_an_unscheduled_definition_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("dl-plain");
        await RegisterAsync(name, ct);

        var before = await ReadAsync(name, ct);
        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            before.Version,
            new JobDefinitionPolicyOverrides(DeadlineSeconds: 444),
            Actor,
            "set deadline",
            ct
        );

        Assert.Equal(ControlAction.Applied, outcome.Action);
        Assert.Equal(444, (await ReadAsync(name, ct)).DeadlineSecondsOverride);
    }
}
