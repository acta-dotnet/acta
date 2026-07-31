using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Asserts the catalog side-effects of <c>WorkerRuntime.InitializeAsync</c> for a generated
/// <c>TestJobs.TestJobsManifest</c>. <c>InitializeAsync</c> ran before the test body via
/// <see cref="ActaRuntimeTestBase{TFixture, TManifest}.AfterInitializeAsync"/>; <c>StartAsync</c>
/// was deliberately NOT called.
/// </summary>
[ConformanceSpec(
    "worker-runtime.initialize-catalog",
    "Init writes namespace worker and full definition policy idempotently",
    Area = "Catalog",
    Contract = "InitializeAsync writes namespace and worker rows with a WorkerStarted event, persists each definition's full policy or framework defaults, and is idempotent.",
    Arrange = "A worker runtime is built from a generated TestJobs manifest, with StartAsync deliberately not called.",
    Act = "InitializeAsync runs against the namespace, then a second init repeats it.",
    Assert = "The namespace, one worker row, a WorkerStarted event, and each definition's full policy or framework defaults are written with no duplicates."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.StartWorkerAsync))]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.RegisterDefinitionsAsync))]
public abstract class WorkerRuntimeRegistrationSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Init assigns a namespace id and writes the namespace row")]
    public async Task Initialize_assigns_namespace_id_and_writes_namespace_row()
    {
        var ct = TestContext.Current.CancellationToken;

        // Runtime.InitializeAsync already ran. StartAsync was *not* called.
        Assert.True(Runtime.RegisteredNamespaceIds.ContainsKey(TestNamespace), $"RegisteredNamespaceIds should contain {TestNamespace}.");
        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];
        Assert.True(assignedId > 0, "DB-assigned namespace id must be positive.");

        var row = await Db.From<JobNamespace>().Where(n => n.Id == assignedId).SingleOrDefaultAsync(ct);

        Assert.NotNull(row);
        Assert.Equal(TestNamespace, row!.Name);
        Assert.Equal("test", row.OwnerTeam);
    }

    [Fact(DisplayName = "Init writes exactly one worker row for this runtime")]
    public async Task Initialize_writes_job_worker_row_for_this_runtime()
    {
        var ct = TestContext.Current.CancellationToken;

        // workers is append-only - every InitializeAsync call inserts a fresh row. For this
        // freshly-allocated per-test namespace, exactly one row should exist.
        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var count = await Db.From<JobWorker>().Where(w => w.NamespaceId == assignedId).CountAsync(ct);
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "Init emits a WorkerStarted event for the worker")]
    public async Task Initialize_emits_worker_started_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == assignedId).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);

        var events = await Db.From<JobEvent>()
            .Where(e => e.WorkerId == worker!.Id && e.EventCode == JobEventCode.WorkerStarted)
            .ToListAsync(ct);
        var startedEvent = Assert.Single(events);
        Assert.Null(startedEvent.JobId);
        Assert.Equal(assignedId, startedEvent.NamespaceId);
        Assert.Equal(JobActorCode.Worker, startedEvent.ActorCode);
        Assert.Null(startedEvent.ReasonCode);
    }

    [Fact(DisplayName = "Full definition policy from the attribute persists verbatim")]
    public async Task Initialize_persists_full_job_definition_policy_from_attribute()
    {
        var ct = TestContext.Current.CancellationToken;
        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var def = await Db.From<JobDefinition>()
            .Where(d => d.NamespaceId == assignedId && d.Name == "policy-probe")
            .SingleOrDefaultAsync(ct);

        Assert.NotNull(def);
        // Attribute-supplied policy lands verbatim (durations resolved to whole seconds).
        Assert.Equal((short)7, def!.MaxAttempts);
        Assert.Equal(JobPriorityCode.High, def.Priority);
        Assert.Equal("30s..2h x3 ±25%", def.Backoff);
        Assert.Equal(45, def.ExecutionTimeoutSeconds);
        Assert.Equal(7 * 24 * 60 * 60, def.JobRetentionSeconds);
        Assert.Equal(JobAuditLevelCode.Off, def.AuditLevel);
        Assert.Equal("ops", def.AlertChannelName);
        Assert.Equal("https://runbook.example/policy-probe", def.RunbookUrl);
        Assert.Equal("Policy Probe", def.DisplayName);
        Assert.Equal("Probes full attribute policy persistence.", def.Description);
    }

    [Fact(DisplayName = "Framework defaults apply when the attribute omits policy")]
    public async Task Initialize_applies_framework_defaults_when_attribute_omits_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];

        // `echo` declares only its name - every policy column falls back to the framework default,
        // and the nullable operator columns stay null.
        var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == assignedId && d.Name == "echo").SingleOrDefaultAsync(ct);

        Assert.NotNull(def);
        Assert.Equal("1m..8h", def!.Backoff);
        Assert.Equal(5 * 60, def.ExecutionTimeoutSeconds);
        Assert.Equal(90 * 24 * 60 * 60, def.JobRetentionSeconds);
        Assert.Null(def.AlertChannelName);
        Assert.Null(def.RunbookUrl);
        Assert.Null(def.DisplayName);
        Assert.Null(def.Description);
    }

    [Fact(DisplayName = "Second init on the same instance does not double-insert the worker")]
    public async Task Initialize_called_twice_on_same_instance_doesnt_double_insert_worker()
    {
        // Pins the in-process retry guard: a second InitializeAsync call on the SAME WorkerRuntime
        // instance does NOT insert a second workers row (and does NOT add a second
        // namespaces row - that's MERGE-by-name). Guards against a future refactor that
        // removes the per-instance _workerInserted tracking. (Cross-process retries - a new
        // WorkerRuntime instance - would correctly produce a second worker row; that's the
        // append-only contract.)
        var ct = TestContext.Current.CancellationToken;
        await Runtime.InitializeAsync(ct);

        var assignedId = Runtime.RegisteredNamespaceIds[TestNamespace];

        // namespace: still exactly one row (upsert-by-name)
        var namespaceCount = await Db.From<JobNamespace>().Where(n => n.Id == assignedId).CountAsync(ct);
        Assert.Equal(1, namespaceCount);

        // worker: in-process retry guard means second call SKIPS the worker insert (since the
        // first call already added it this lifetime). So still 1 row.
        var workerCount = await Db.From<JobWorker>().Where(w => w.NamespaceId == assignedId).CountAsync(ct);
        Assert.Equal(1, workerCount);
    }
}
