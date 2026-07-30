using System.Collections.Immutable;
using Acta.Configuration;
using Acta.Modules.Execution.Definitions;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for monotonic job-definition promotion: newer-or-equal generations may update policy
/// and contract; older generations are read-only and cannot retire or rewrite newer rows.
/// </summary>
[ConformanceSpec(
    "register-definitions.monotonic-promotion",
    "Newer-or-equal generation promotes policy; older cannot downgrade or retire",
    Area = "Catalog",
    Contract = "Writes a definition only when the incoming manifest generation is at or above the stored one, never downgrading or retiring on an older generation.",
    Arrange = "A job definition is stored at a known manifest generation.",
    Act = "The definition is re-registered at newer, equal, and older manifest generations.",
    Assert = "Newer or equal generations update policy and retirement while older generations leave the stored row unchanged."
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
        var (db, dialect) = Store();
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
        var (db, dialect) = Store();
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
        var (db, dialect) = Store();
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
        var (db, dialect) = Store();
        var name = TestKey("idempotent");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var first = await ReadAsync(name, ct);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(name, 4, typeof(int))], ct);
        var second = await ReadAsync(name, ct);

        Assert.Equal(first.Version, second.Version);
    }

    [Fact(DisplayName = "Older generation does not retire a newer definition it omits")]
    public async Task Older_worker_does_not_retire_a_newer_definition_it_omits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var keep = TestKey("keep");
        var newer = TestKey("newer-only");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(keep, 1, typeof(int)), Def(newer, 1, typeof(int))], ct);
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(keep, 1, typeof(int))], ct);

        var newerDef = await ReadAsync(newer, ct);
        Assert.Equal(JobDefinitionStatusCode.Active, newerDef.Status);
    }

    [Fact(DisplayName = "Equal or newer generation retires a genuinely removed definition")]
    public async Task Equal_or_newer_worker_retires_a_genuinely_removed_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var keep = TestKey("retire-keep");
        var gone = TestKey("retire-gone");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(keep, 1, typeof(int)), Def(gone, 1, typeof(int))], ct);
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(keep, 1, typeof(int))], ct);

        var goneDef = await ReadAsync(gone, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, goneDef.Status);
    }

    [Fact(DisplayName = "Older generation cannot reactivate or rewrite a newer retired definition")]
    public async Task Older_worker_cannot_reactivate_or_rewrite_a_newer_retired_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var job = TestKey("react-job");
        var other = TestKey("react-other");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(job, 1, typeof(int)), Def(other, 1, typeof(int))], ct);
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [Def(other, 1, typeof(int))], ct);
        var retired = await ReadAsync(job, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, retired.Status);

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [Def(job, 7, typeof(int))], ct);
        var still = await ReadAsync(job, ct);
        Assert.Equal(JobDefinitionStatusCode.Retired, still.Status);
        Assert.Equal(retired.Version, still.Version);
    }

    [Fact(DisplayName = "Retirement cancels the definition's parked jobs with reason definition-retired")]
    public async Task Retirement_cancels_parked_jobs_with_definition_retired_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = TestKey("ret-keep");
        var gone = TestKey("ret-gone");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [AuditedDef(keep), AuditedDef(gone)], ct);

        var jobs = Services.GetRequiredService<IJobs>();
        var payload = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Json.Id).Serialize(1);
        var ready = await jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, gone, payload, null, null, null), ct);
        var paused = await jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, gone, payload, null, null, null), ct);
        Assert.Equal(JobControlAction.Applied, (await jobs.PauseAsync(paused, null, null, ct)).Action);
        var kept = await jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, keep, payload, null, null, null), ct);

        // Re-register without 'gone': the same transaction that retires the definition cancels its
        // parked rows set-wise; the surviving definition's job is untouched.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [AuditedDef(keep)], ct);

        Assert.Equal(JobStatusCode.Ready, await jobs.GetStatusAsync(kept, ct));
        foreach (var cancelled in new[] { ready, paused })
        {
            Assert.Equal(JobStatusCode.Cancelled, await jobs.GetStatusAsync(cancelled, ct));

            var runtime = await Db.From<JobRuntime>().Where(r => r.Id == cancelled.JobId).SingleOrDefaultAsync(ct);
            Assert.NotNull(runtime!.RetentionUntilUtc);

            var ev = await Db.From<JobEvent>()
                .Where(e => e.JobId == cancelled.JobId && e.EventCode == JobEventCode.JobCancelled)
                .SingleOrDefaultAsync(ct);
            Assert.NotNull(ev);
            Assert.Equal(JobEventReasonCode.JobDefinitionRetired, ev!.ReasonCode);
        }
    }

    [Fact(DisplayName = "A later registration does not re-cancel a re-armed job under an already-retired definition")]
    public async Task Later_registration_does_not_resweep_an_already_retired_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = TestKey("resweep-keep");
        var gone = TestKey("resweep-gone");

        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen1, [AuditedDef(keep), AuditedDef(gone)], ct);

        var jobs = Services.GetRequiredService<IJobs>();
        var payload = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Json.Id).Serialize(1);
        var job = await jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, gone, payload, null, null, null), ct);

        // First registration without 'gone' retires it and cancels its parked job.
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [AuditedDef(keep)], ct);
        Assert.Equal(JobStatusCode.Cancelled, await jobs.GetStatusAsync(job, ct));

        // Operator restarts the cancelled job: it is parked (Ready) again under the already-retired definition.
        Assert.Equal(JobControlAction.Applied, (await jobs.RestartAsync(job, null, null, ct)).Action);
        Assert.Equal(JobStatusCode.Ready, await jobs.GetStatusAsync(job, ct));

        // A later registration that changes 'keep' runs the routine again, but 'gone' did not transition
        // this call, so its re-armed job must not be re-swept. (Changing 'keep' defeats the no-op write gate.)
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Gen2, [AuditedDef(keep, 2)], ct);

        Assert.Equal(JobStatusCode.Ready, await jobs.GetStatusAsync(job, ct));

        // Exactly one cancellation event exists (from the first registration); the later call emitted none.
        var ev = await Db.From<JobEvent>()
            .Where(e => e.JobId == job.JobId && e.EventCode == JobEventCode.JobCancelled)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(ev);
    }

    // Retirement cancel-sweep events are audit-gated like every control verb, so this needs a real
    // audit level (Def's default(0) is not Audit) and a concrete retention for the stamp assert.
    private static JobDescriptor AuditedDef(string name, short maxAttempts = 1) =>
        Def(name, maxAttempts, typeof(int)) with
        {
            AuditLevel = JobAuditLevelCode.Audit,
            JobRetentionSeconds = 3600,
        };

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
