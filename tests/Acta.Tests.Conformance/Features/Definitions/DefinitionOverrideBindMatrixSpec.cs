using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Pins the full 13-column override bind matrix of <c>set_job_definition_overrides</c>:
/// each <c>*_override</c> column receives exactly the value written and each <c>*_effective</c>
/// is <c>COALESCE(override, base)</c>. Distinct values per column make any cross-wiring detectable.
/// </summary>
[ConformanceSpec(
    "catalog.override-bind-matrix",
    "Definition override bind matrix: all 13 slots",
    Area = "Catalog",
    Contract = "All 13 override slots bind to their own column, COALESCE recomputes each effective, and null clears the override to fall back to base.",
    Arrange = "A definition is registered with well-known base policy values behind all 13 override slots.",
    Act = "SetJobDefinitionOverrides sets all 13 overrides to distinct values in one call and a second call clears them all.",
    Assert = "Each override binds to its own column with effective recomputed by COALESCE, and clearing reverts every effective to its base value."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.SetDefinitionOverridesAsync))]
public abstract class DefinitionOverrideBindMatrixSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static JobControlActor Actor => new(JobActorCode.Operator, "tester");

    // Def() registers with well-known base values so the clear fact can assert exact effective fallbacks.
    // Base policy: Priority=Bulk(0), MaxAttempts=3, AuditLevel=Off(0), AlertProfile=None(0).
    // Framework defaults fill Backoff="1m..1d x2 ~10%", ExecutionTimeout=300, JobRetention=7776000;
    // DeadlineSeconds=0, DeadlineBehavior=Strict.
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
            Priority: default, // Bulk = 0
            MaxAttempts: 3,
            AuditLevel: default, // Off = 0
            AlertProfile: default, // None = 0
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

    private async Task<int> RegisterAsync(string name, CancellationToken ct)
    {
        var (_, _) = Store();
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen, [Def(name)], ct);
        return (await ReadAsync(name, ct)).Id;
    }

    [Fact(DisplayName = "All 13 overrides bind to their own column, detectable by distinct values")]
    public async Task All_13_overrides_bind_to_their_own_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("ovr-matrix");
        var id = await RegisterAsync(name, ct);
        var before = await ReadAsync(name, ct);

        // Each int/decimal/code override is a distinct value so any cross-wiring (e.g.
        // backoff_initial landing in backoff_max) would produce a wrong assertion.
        var channelName = "chan-" + TestId;
        var runbookUrl = "https://rb/" + TestId;
        var displayName = "Renamed By Operator";
        var description = "Operator description.";

        var outcome = await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            id,
            before.Version,
            new JobDefinitionPolicyOverrides(
                Priority: JobPriorityCode.High, // ≠ Bulk base
                MaxAttempts: 7, // ≠ 3 base
                Backoff: "5s..9m x4 +-20%", // distinct expression
                ExecutionTimeoutSeconds: 33, // distinct int 3
                DeadlineSeconds: 444, // distinct int 4
                DeadlineBehavior: DeadlineBehaviorCode.Advisory, // ≠ Strict base
                JobRetentionSeconds: 555, // distinct int 5
                AuditLevel: JobAuditLevelCode.Audit, // ≠ Off base
                AlertProfile: JobAlertProfileCode.OnTerminal, // ≠ None base
                AlertChannelName: channelName,
                RunbookUrl: runbookUrl,
                DisplayName: displayName,
                Description: description
            ),
            Actor,
            "set all 13",
            ct
        );

        Assert.Equal(JobControlAction.Applied, outcome.Action);

        var after = await ReadAsync(name, ct);

        // --- Override columns: exactly what was written ---
        Assert.Equal(JobPriorityCode.High, after.PriorityOverride);
        Assert.Equal((short)7, after.MaxAttemptsOverride);
        Assert.Equal("5s..9m x4 +-20%", after.BackoffOverride);
        Assert.Equal(33, after.ExecutionTimeoutSecondsOverride);
        Assert.Equal(444, after.DeadlineSecondsOverride);
        Assert.Equal(DeadlineBehaviorCode.Advisory, after.DeadlineBehaviorOverride);
        Assert.Equal(555, after.JobRetentionSecondsOverride);
        Assert.Equal(JobAuditLevelCode.Audit, after.AuditLevelOverride);
        Assert.Equal(JobAlertProfileCode.OnTerminal, after.AlertProfileOverride);
        Assert.Equal(channelName, after.AlertChannelNameOverride);
        Assert.Equal(runbookUrl, after.RunbookUrlOverride);
        Assert.Equal(displayName, after.DisplayNameOverride);
        Assert.Equal(description, after.DescriptionOverride);

        // --- Effective columns: COALESCE(override, base) = override when set ---
        Assert.Equal(JobPriorityCode.High, after.PriorityEffective);
        Assert.Equal((short)7, after.MaxAttemptsEffective);
        Assert.Equal("5s..9m x4 +-20%", after.BackoffEffective);
        Assert.Equal(33, after.ExecutionTimeoutSecondsEffective);
        Assert.Equal(444, after.DeadlineSecondsEffective);
        Assert.Equal(DeadlineBehaviorCode.Advisory, after.DeadlineBehaviorEffective);
        Assert.Equal(555, after.JobRetentionSecondsEffective);
        Assert.Equal(JobAuditLevelCode.Audit, after.AuditLevelEffective);
        Assert.Equal(JobAlertProfileCode.OnTerminal, after.AlertProfileEffective);
        Assert.Equal(channelName, after.AlertChannelNameEffective);
        Assert.Equal(runbookUrl, after.RunbookUrlEffective);
        Assert.Equal(displayName, after.DisplayNameEffective);
        Assert.Equal(description, after.DescriptionEffective);

        // Base defaults untouched
        Assert.Equal(JobPriorityCode.Bulk, after.Priority);
        Assert.Equal((short)3, after.MaxAttempts);
    }

    [Fact(DisplayName = "Clearing all overrides reverts each effective to its base value")]
    public async Task Clearing_all_overrides_reverts_to_base()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _) = Store();
        var name = TestKey("ovr-clear");
        var id = await RegisterAsync(name, ct);

        var v0 = (await ReadAsync(name, ct)).Version;
        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            id,
            v0,
            new JobDefinitionPolicyOverrides(
                Priority: JobPriorityCode.High,
                MaxAttempts: 7,
                Backoff: "5s..9m x4 +-20%",
                ExecutionTimeoutSeconds: 33,
                DeadlineSeconds: 444,
                DeadlineBehavior: DeadlineBehaviorCode.Advisory,
                JobRetentionSeconds: 555,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: JobAlertProfileCode.OnTerminal,
                AlertChannelName: "chan-" + TestId,
                RunbookUrl: "https://rb/" + TestId,
                DisplayName: "Renamed By Operator",
                Description: "Operator description."
            ),
            Actor,
            "set all 13",
            ct
        );

        var set = await ReadAsync(name, ct);
        await DefinitionTestOps.UpdateOverridesAsync(Services, id, set.Version, new JobDefinitionPolicyOverrides(), Actor, "clear all", ct);

        var cleared = await ReadAsync(name, ct);

        // --- All override columns must be null ---
        Assert.Null(cleared.PriorityOverride);
        Assert.Null(cleared.MaxAttemptsOverride);
        Assert.Null(cleared.BackoffOverride);
        Assert.Null(cleared.ExecutionTimeoutSecondsOverride);
        Assert.Null(cleared.DeadlineSecondsOverride);
        Assert.Null(cleared.DeadlineBehaviorOverride);
        Assert.Null(cleared.JobRetentionSecondsOverride);
        Assert.Null(cleared.AuditLevelOverride);
        Assert.Null(cleared.AlertProfileOverride);
        Assert.Null(cleared.AlertChannelNameOverride);
        Assert.Null(cleared.RunbookUrlOverride);
        Assert.Null(cleared.DisplayNameOverride);
        Assert.Null(cleared.DescriptionOverride);

        // --- Effective falls back to base values ---
        Assert.Equal(JobPriorityCode.Bulk, cleared.PriorityEffective);
        Assert.Equal((short)3, cleared.MaxAttemptsEffective);
        Assert.Equal("1m..1d x2 ~10%", cleared.BackoffEffective); // framework default
        Assert.Equal(300, cleared.ExecutionTimeoutSecondsEffective); // framework default
        Assert.Equal(0, cleared.DeadlineSecondsEffective); // no deadline
        Assert.Equal(DeadlineBehaviorCode.Strict, cleared.DeadlineBehaviorEffective);
        Assert.Equal(7776000, cleared.JobRetentionSecondsEffective); // framework default
        Assert.Equal(JobAuditLevelCode.Off, cleared.AuditLevelEffective);
        Assert.Equal(JobAlertProfileCode.None, cleared.AlertProfileEffective);
        Assert.Null(cleared.AlertChannelNameEffective);
        Assert.Null(cleared.RunbookUrlEffective);
        Assert.Null(cleared.DisplayNameEffective); // Def() sets no [Job] DisplayName
        Assert.Null(cleared.DescriptionEffective); // Def() sets no [Job] Description
    }
}
