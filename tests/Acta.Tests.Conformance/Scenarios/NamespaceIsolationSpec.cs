using System.Data.Common;
using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A worker serves one namespace, and every operation it or an operator runs acts on one namespace, so none of them
/// may read, and so lock, another namespace's rows. Another namespace is filled with work, an open transaction then
/// holds every one of its rows exclusively, and each per-namespace operation runs against the test namespace with a
/// deadline: one that waits reads rows it has no business with, which on SQL Server means a scan where a seek belongs
/// (docs/sql-internals.md, "Locking reads seek"). The spec runs in a schema of its own, where tables stay small enough
/// that a scan is the cheaper plan, so a statement that does not seek shows here. One fact per area, so a failure names
/// the area. SQLite runs one writer at a time. Tenants carry no namespace. The outbox's staging table is the
/// producer's own and carries no namespace; what its relay writes is an enqueue, checked here.
/// </summary>
[ConformanceSpec(
    "namespaces.isolation",
    "Work in one namespace never waits on another namespace's rows",
    Area = "Namespaces",
    Contract = "With every row of another namespace held by an open transaction, each per-namespace operation completes without waiting on it."
)]
public sealed class NamespaceIsolationSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    // An operation that seeks finishes in milliseconds; one that waits on the held rows never does.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    protected override bool OwnSchema => true;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A worker's execution paths complete while another namespace's rows are held")]
    public async Task Execution_never_waits_on_another_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var other = await HeldNamespaceAsync(ct);

        // Work the checks below act on, made before the hold so only the operation under test runs inside it.
        var tenant = (await Services.GetRequiredService<ITenants>().RegisterAsync(Unique("tenant"), ct: ct)).TenantName;
        var failed = await Jobs.EnqueueAsync(Request("on-terminal-probe", null), ct);
        await RunToEndAsync(failed, ct);
        var alertsOwner = await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 2), ct);
        // Behind the projection's horizon, so the alerts pass below projects the failure.
        await AlertTestOps.AgeEventsPastHorizonAsync(Services, NamespaceId, ct);
        var forReset = await Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(2, 2)) with { DelaySeconds = 3600 }, ct);
        var resetFence = await ExecutionFenceStaging.ExecutingAsync(Db, forReset.JobId, ct);
        // The recurring job's schedule comes due a minute ago, so admission has an occurrence to settle.
        var schedules = Services.GetRequiredService<ISchedules>();
        var recurring = new ScheduleLookup(TestNamespace, "recurring-ping", "every-5-minutes");
        var slot = await ScheduleTestOps.SlotIdAsync(Db, NamespaceId, "recurring-ping", ct);
        var due = DateTime.UtcNow.AddMinutes(-1);
        var scheduleIds = (await Db.From<ScheduleEntity>().Where(s => s.JobId == slot).ToListAsync(ct)).Select(s => s.Id).ToList();
        await Db.From<ScheduleStateEntity>()
            .Where(s => scheduleIds.Contains(s.Id))
            .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = due, NextEvaluationAtUtc = due }, ct);
        // A finished, tagged job past its retention, so the sweep deletes a job and its tags.
        var expired = await EnqueueAndRunAsync("add-numbers", new AddNumbers(4, 4), ct);
        await Services.GetRequiredService<ITags>().UpsertAsync(TagTarget.ForJob(expired), new TagInput("isolation", "expired"), ct);
        await Db.From<JobStateEntity>()
            .Where(r => r.Id == expired.JobId)
            .UpdateOnlyAsync(() => new JobStateEntity { RetentionUntilUtc = DateTime.UtcNow.AddDays(-1) }, ct);
        var stranded = await Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(3, 3)), ct);
        await ChaosSpecHelpers.StrandExecutingAsync(
            Services,
            NamespaceId,
            await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct),
            stranded.JobId,
            ct
        );

        await other.HoldAsync(ct);
        await other.Check("enqueue", t => Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(1, 1)), t).AsTask());
        await other.Check(
            "enqueue with a deduplication key",
            t => Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(1, 2)) with { DeduplicationKey = Unique("dedup") }, t).AsTask()
        );
        await other.Check(
            "enqueue a batch",
            t =>
                Jobs.EnqueueJobsAsync([Request("add-numbers", new AddNumbers(2, 2)), Request("add-numbers", new AddNumbers(2, 3))], t)
                    .AsTask()
        );
        await other.Check(
            "enqueue into a lane",
            t => Jobs.EnqueueAsync(Request("lane-step", new LaneStep("held", "one")) with { Lane = "held" }, t).AsTask()
        );
        await other.Check(
            "claim and complete",
            async t => await Runtime.RunOnceAsync(TestNamespace, (await Jobs.EnqueueAsync(Request("echo", new Echo("hi")), t)).JobId, t)
        );
        await other.Check("run a step", t => RunToEndAsync(Request("job-step-basic", null), t));
        await other.Check("run a child and resume its parent", t => RunToEndAsync(Request("job-parent-one", null), t));
        await other.Check("fail, retry, and succeed", t => RunToEndAsync(Request("flaky-recover", null), t));
        await other.Check(
            "wait for a signal, raise it, and resume",
            async t =>
            {
                var waiter = await Jobs.EnqueueAsync(Request("job-wait-signal", null), t);
                await Runtime.RunOnceAsync(TestNamespace, waiter.JobId, t);
                await Jobs.RaiseSignalAsync(waiter, "go", true, ct: t);
                await RunToEndAsync(waiter, t);
            }
        );
        await other.Check(
            "sleep",
            async t =>
                Assert.Equal(
                    RunOnceStatus.Rearmed,
                    await Runtime.RunOnceAsync(TestNamespace, (await Jobs.EnqueueAsync(Request("job-sleep-basic", null), t)).JobId, t)
                )
        );
        await other.Check("write notes", t => RunToEndAsync(Request("job-note", null), t));
        await other.Check("run under a concurrency limit", t => RunToEndAsync(Request("concurrency-limit-probe", null), t));
        await other.Check("run under a rate limit", t => RunToEndAsync(Request("rate-limited-probe", null), t));
        await other.Check(
            "run a tenant's job",
            t => RunToEndAsync(Request("tenant-required-probe", new TenantScopedWork("w")) with { TenantName = tenant }, t)
        );
        await other.Check(
            "fire a recurring job",
            async t =>
            {
                Applied((await schedules.TriggerAsync(recurring, ct: t)).Action, "trigger");
                Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, slot, t));
            }
        );
        // After the firing, while the slot is idle again: admission admits the late occurrence or skips it, and either
        // way moves the schedule past it.
        await other.Check(
            "admit the namespace's due occurrence",
            async t =>
            {
                await Runtime.AdmitOnceAsync(TestNamespace, t);
                Assert.True((await schedules.GetAsync(recurring, t))!.NextOccurrenceAtUtc > due, "admission left the occurrence due");
            }
        );
        await other.Check(
            "hold a named lock across a heartbeat",
            async t =>
            {
                LockHolder.Reset(TestNamespace);
                var holder = await Jobs.EnqueueAsync(Request("lock-holder", null), t);
                var run = Runtime.RunOnceAsync(TestNamespace, holder.JobId, t);
                try
                {
                    await LockHolder.Entered(TestNamespace).WaitAsync(t);
                    Assert.True(await Runtime.RunHeartbeatOnceAsync(t), "the heartbeat pass was not answered");
                }
                finally
                {
                    LockHolder.Release(TestNamespace);
                }
                Assert.Equal(RunOnceStatus.Completed, await run);
            }
        );
        await other.Check(
            "reset a job's state",
            t => Services.GetRequiredService<ResetJobStateHandler>().HandleAsync(new ResetJobStateCommand(resetFence), t)
        );
        await other.Check(
            "recover a stranded job",
            async t =>
                Assert.True(
                    (await Services.GetRequiredService<RecoveryPass>().RunAsync(NamespaceId, TestNamespace, t)).ReclaimedJobs > 0,
                    "the recovery pass reclaimed nothing"
                )
        );
        await other.Check(
            "project and deliver alerts",
            t =>
                AlertTestOps.RunAlertsJobAsync(
                    Services,
                    TestNamespace,
                    NamespaceId,
                    alertsOwner.JobId,
                    options: null,
                    drain: null,
                    t,
                    ageEventsPastHorizon: false
                )
        );
        await other.Check(
            "purge an expired job and its tags",
            async t =>
            {
                var purged = await RetentionTestOps.PurgeAsync(
                    Services,
                    NamespaceId,
                    eventsRetentionDays: 30,
                    alertRetentionDays: 30,
                    workerRetentionSeconds: 86400,
                    batchSize: 100,
                    maxIterations: 50,
                    t
                );
                Assert.True(purged.Jobs > 0, "the sweep purged no job");
            }
        );
        // Last: the worker serves nothing after it stops.
        await other.Check("stop the worker", t => Runtime.StopAsync(t));
        await other.ReleaseAsync(ct);

        var projected = await Services.GetRequiredService<IAlerts>().ListAsync(new ListAlertsQuery(TestNamespace, failed.JobId), ct);
        Assert.True(projected.Items.Count > 0, "the alerts pass projected no alert for the failed job");
    }

    [Fact(DisplayName = "Job controls complete while another namespace's rows are held")]
    public async Task Job_controls_never_wait_on_another_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var other = await HeldNamespaceAsync(ct);

        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 2), ct);
        var forPurge = await EnqueueAndRunAsync("add-numbers", new AddNumbers(3, 4), ct);
        // Due in an hour, so nothing claims them and each control meets a job it can act on.
        var ready = await Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(5, 6)) with { DelaySeconds = 3600 }, ct);
        var forPause = await Jobs.EnqueueAsync(Request("add-numbers", new AddNumbers(7, 8)) with { DelaySeconds = 3600 }, ct);

        await other.HoldAsync(ct);
        await other.Check("cancel", async t => Applied((await Jobs.CancelAsync(ready, ct: t)).Action, "cancel"));
        await other.Check(
            "pause and resume",
            async t =>
            {
                Applied((await Jobs.PauseAsync(forPause, ct: t)).Action, "pause");
                Applied((await Jobs.ResumeAsync(forPause, ct: t)).Action, "resume");
            }
        );
        await other.Check(
            "reschedule",
            async t => Applied((await Jobs.RescheduleAsync(forPause, DateTime.UtcNow.AddMinutes(90), ct: t)).Action, "reschedule")
        );
        await other.Check(
            "update a job's input",
            async t => Applied((await Jobs.UpdateInputAsync(forPause, JobPayload.Json(new AddNumbers(9, 9)), ct: t)).Action, "input update")
        );
        await other.Check(
            "reprioritize",
            async t => Applied((await Jobs.ReprioritizeAsync(forPause, JobPriorityCode.High, ct: t)).Action, "reprioritize")
        );
        await other.Check("restart", async t => Applied((await Jobs.RestartAsync(completed, ct: t)).Action, "restart"));
        await other.Check("purge", async t => Applied((await Jobs.PurgeAsync(forPurge, "isolation spec", ct: t)).Action, "purge"));
        await other.ReleaseAsync(ct);
    }

    [Fact(DisplayName = "Operator verbs complete while another namespace's rows are held")]
    public async Task Operator_verbs_never_wait_on_another_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var other = await HeldNamespaceAsync(ct);

        var definitions = Services.GetRequiredService<IDefinitions>();
        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 2), ct);
        var definitionVersion = (await definitions.GetAsync(TestNamespace, "add-numbers", ct))!.Version;
        var retireVersion = (await definitions.GetAsync(TestNamespace, "job-note", ct))!.Version;
        var alert = await RaiseAlertAsync(Services, TestNamespace, completed.JobId, ct);
        var schedule = new ScheduleLookup(TestNamespace, "recurring-ping", "every-5-minutes");

        await other.HoldAsync(ct);
        await other.Check(
            "update a definition's overrides",
            async t =>
            {
                var updated = await DefinitionTestOps.UpdateOverridesAsync(
                    Services,
                    TestNamespace,
                    "add-numbers",
                    definitionVersion,
                    new DefinitionPolicyOverrides(MaxAttempts: 3),
                    "test",
                    null,
                    t
                );
                Applied(updated.Action, "overrides update");
            }
        );
        await other.Check("deploy", t => CatalogTestOps.DeployAsync(Services, t));
        var schedules = Services.GetRequiredService<ISchedules>();
        await other.Check("trigger a schedule", async t => Applied((await schedules.TriggerAsync(schedule, ct: t)).Action, "trigger"));
        await other.Check(
            "pause and resume a schedule",
            async t =>
            {
                Applied((await schedules.PauseAsync(schedule, ct: t)).Action, "schedule pause");
                Applied((await schedules.ResumeAsync(schedule, ct: t)).Action, "schedule resume");
            }
        );
        await other.Check(
            "retire a definition",
            async t => Applied((await definitions.RetireAsync(TestNamespace, "job-note", retireVersion, ct: t)).Action, "retire")
        );
        await other.Check(
            "deploy and record the version",
            t => Services.GetRequiredService<CatalogDeployer>().DeployAsync(force: false, t)
        );
        var namespaces = Services.GetRequiredService<INamespaces>();
        await other.Check(
            "update, suspend, and resume the namespace",
            async t =>
            {
                var version = (await namespaces.GetAsync(TestNamespace, t))!.Version;
                Applied((await namespaces.UpdateAsync(TestNamespace, version, "test", "isolation spec", ct: t)).Action, "namespace update");
                Applied((await namespaces.SuspendAsync(TestNamespace, ct: t)).Action, "namespace suspend");
                Applied((await namespaces.ResumeAsync(TestNamespace, ct: t)).Action, "namespace resume");
            }
        );
        var tenants = Services.GetRequiredService<ITenants>();
        await other.Check(
            "register, update, suspend, and resume a tenant",
            async t =>
            {
                var tenant = (await tenants.RegisterAsync(Unique("tenant"), ct: t)).TenantName;
                var version = (await tenants.GetAsync(tenant, t))!.Version;
                Applied((await tenants.UpdateAsync(tenant, version, "Isolation", "isolation spec", ct: t)).Action, "tenant update");
                Applied((await tenants.SuspendAsync(tenant, ct: t)).Action, "tenant suspend");
                Applied((await tenants.ResumeAsync(tenant, ct: t)).Action, "tenant resume");
            }
        );
        await other.Check(
            "tag a job, a definition, and the namespace, and replace the namespace's tags",
            async t =>
            {
                await TagAsync(Services, TestNamespace, completed, t);
                var tags = Services.GetRequiredService<ITags>();
                Applied(
                    (await tags.ReplaceAsync(TagTarget.ForNamespace(TestNamespace), [new TagInput("isolation", "replaced")], t)).Action,
                    "tag replace"
                );
            }
        );
        await other.Check(
            "save, publish, and archive a calendar",
            async t =>
            {
                var calendar = new CalendarLookup(TestNamespace, Unique("calendar"));
                await PublishCalendarAsync(Services, calendar, t);
                Applied((await Services.GetRequiredService<ICalendars>().ArchiveAsync(calendar, ct: t)).Action, "calendar archive");
            }
        );
        var alerts = Services.GetRequiredService<IAlerts>();
        await other.Check(
            "acknowledge and resolve an alert",
            async t =>
            {
                Applied((await alerts.AcknowledgeAsync(alert, ct: t)).Action, "alert acknowledge");
                Applied((await alerts.ResolveAsync(alert, ct: t)).Action, "alert resolve");
            }
        );
        await other.ReleaseAsync(ct);
    }

    [Fact(DisplayName = "Reads and lookups complete while another namespace's rows are held")]
    public async Task Reads_never_wait_on_another_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var other = await HeldNamespaceAsync(ct);

        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 2), ct);
        var parent = await Jobs.EnqueueAsync(Request("job-parent-one", null), ct);
        await RunToEndAsync(parent, ct);
        var deduplicationKey = Unique("dedup");
        await Jobs.EnqueueAsync(
            Request("add-numbers", new AddNumbers(5, 6)) with
            {
                DelaySeconds = 3600,
                DeduplicationKey = deduplicationKey,
            },
            ct
        );
        var alert = await RaiseAlertAsync(Services, TestNamespace, completed.JobId, ct);
        await TagAsync(Services, TestNamespace, completed, ct);
        var calendar = new CalendarLookup(TestNamespace, Unique("calendar"));
        await PublishCalendarAsync(Services, calendar, ct);
        var tenant = (await Services.GetRequiredService<ITenants>().RegisterAsync(Unique("tenant"), ct: ct)).TenantName;
        var workers = Services.GetRequiredService<IWorkers>();
        var worker = (await workers.ListAsync(new ListWorkersQuery(TestNamespace), ct)).Items[0].WorkerRef;

        await other.HoldAsync(ct);
        await other.Check("read a job", async t => Assert.NotNull(await Jobs.GetAsync(completed, t)));
        await other.Check("read a job by its ref", async t => Assert.NotNull(await Jobs.GetAsync(JobLookup.ByRef(completed.JobRef), t)));
        await other.Check(
            "read a job by its deduplication key",
            async t => Assert.NotNull(await Jobs.GetAsync(JobLookup.ByDeduplicationKey(TestNamespace, deduplicationKey), t))
        );
        await other.Check("read a job's input", async t => Assert.NotNull(await Jobs.GetInputAsync(completed, t)));
        await other.Check("explain a job", async t => Assert.NotNull(await Jobs.ExplainAsync(completed, t)));
        await other.Check("list a job's steps", t => Jobs.ListStepsAsync(new ListJobStepsQuery(completed), t).AsTask());
        await other.Check("list a job's checkpoints", t => Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(parent), t).AsTask());
        await other.Check("map a job's lineage", async t => Assert.NotNull(await Jobs.GetLineageMapAsync(parent, ct: t)));
        var definitions = Services.GetRequiredService<IDefinitions>();
        await other.Check("read a definition", async t => Assert.NotNull(await definitions.GetAsync(TestNamespace, "add-numbers", t)));
        await other.Check(
            "list the namespace's definitions",
            t => definitions.ListAsync(new ListDefinitionsQuery(JobNamespace: TestNamespace, IncludeTotal: true), t).AsTask()
        );
        var schedules = Services.GetRequiredService<ISchedules>();
        await other.Check(
            "read a schedule",
            async t => Assert.NotNull(await schedules.GetAsync(new ScheduleLookup(TestNamespace, "recurring-ping", "every-5-minutes"), t))
        );
        await other.Check(
            "list the namespace's schedules",
            t => schedules.ListAsync(new ListSchedulesQuery(JobNamespace: TestNamespace, IncludeTotal: true), t).AsTask()
        );
        var alerts = Services.GetRequiredService<IAlerts>();
        await other.Check("read an alert", async t => Assert.NotNull(await alerts.GetAsync(alert, t)));
        await other.Check(
            "list the namespace's alerts",
            t => alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, IncludeTotal: true), t).AsTask()
        );
        await other.Check(
            "list the namespace's jobs",
            t => Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, IncludeTotal: true), t).AsTask()
        );
        await other.Check(
            "list the namespace's events",
            t => Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobNamespace: TestNamespace), t).AsTask()
        );
        await other.Check(
            "read the namespace's overview",
            t => Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace), t).AsTask()
        );
        await other.Check(
            "list the namespace's workers",
            t => workers.ListAsync(new ListWorkersQuery(TestNamespace, IncludeTotal: true), t).AsTask()
        );
        await other.Check("read a worker", async t => Assert.NotNull(await workers.GetAsync(worker, t)));
        await other.Check(
            "list the namespace's tags",
            t =>
                Services
                    .GetRequiredService<ITags>()
                    .ListAsync(new ListTagsQuery(TagTarget.ForNamespace(TestNamespace), IncludeTotal: true), t)
                    .AsTask()
        );
        await other.Check("read a tenant", async t => Assert.NotNull(await Services.GetRequiredService<ITenants>().GetAsync(tenant, t)));
        var calendars = Services.GetRequiredService<ICalendars>();
        await other.Check("read a calendar", async t => Assert.NotNull(await calendars.GetAsync(calendar, ct: t)));
        await other.Check(
            "list a calendar's versions",
            t => calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, IncludeTotal: true), t).AsTask()
        );
        await other.Check(
            "list a calendar's dependents",
            t => calendars.ListDependentsAsync(new ListCalendarDependentsQuery(calendar, IncludeTotal: true), t).AsTask()
        );
        await other.Check(
            "list the namespace's calendars",
            t => calendars.ListAsync(new ListCalendarsQuery(TestNamespace, IncludeTotal: true), t).AsTask()
        );
        await other.Check(
            "load the namespace's published calendars",
            t => Services.GetRequiredService<CalendarsOperations>().ListCurrentCalendarsAsync(NamespaceId, t)
        );
        await other.ReleaseAsync(ct);
    }

    /// <summary>A control that did not apply exercised nothing, so the check must not pass on it.</summary>
    private static void Applied(ControlAction action, string control) =>
        Assert.True(action == ControlAction.Applied, $"{control} answered {action}");

    private EnqueueJobRequest Request(string jobName, object? input) =>
        new(TestNamespace, jobName, input is null ? JobPayload.Json(new { }) : JobPayload.Json(input));

    private async Task RunToEndAsync(EnqueueJobRequest request, CancellationToken ct) =>
        await RunToEndAsync(await Jobs.EnqueueAsync(request, ct), ct);

    /// <summary>Runs the test namespace until <paramref name="job"/> is terminal, children and retries included.</summary>
    private async Task RunToEndAsync(EnqueueJobResponse job, CancellationToken ct)
    {
        while (
            (await Jobs.GetStatusAsync(job, ct))?.Status is not (JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled)
        )
        {
            if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceStatus.NothingClaimed)
            {
                await Task.Delay(25, ct);
            }
        }
    }

    private static async Task<AlertRef> RaiseAlertAsync(IServiceProvider services, string jobNamespace, long jobId, CancellationToken ct)
    {
        await AlertTestOps.RaiseAsync(
            services,
            jobNamespace,
            jobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Error,
            AlertKindCode.Manual,
            "isolation",
            "isolation spec",
            "log",
            AlertDeliveryStatusCode.Pending,
            "isolation-" + jobId.ToString(CultureInfo.InvariantCulture),
            ct
        );
        return (await services.GetRequiredService<IAlerts>().ListAsync(new ListAlertsQuery(jobNamespace, jobId), ct)).Items[0].AlertRef;
    }

    private static async Task TagAsync(IServiceProvider services, string jobNamespace, EnqueueJobResponse job, CancellationToken ct)
    {
        var tags = services.GetRequiredService<ITags>();
        Applied((await tags.UpsertAsync(TagTarget.ForJob(job), new TagInput("isolation", "job"), ct)).Action, "job tag");
        Applied(
            (await tags.UpsertAsync(TagTarget.ForDefinition(jobNamespace, "add-numbers"), new TagInput("isolation"), ct)).Action,
            "definition tag"
        );
        Applied((await tags.UpsertAsync(TagTarget.ForNamespace(jobNamespace), new TagInput("isolation"), ct)).Action, "namespace tag");
    }

    /// <summary>Saves a one-instant occurrence calendar and publishes it.</summary>
    private static async Task PublishCalendarAsync(IServiceProvider services, CalendarLookup calendar, CancellationToken ct)
    {
        var calendars = services.GetRequiredService<ICalendars>();
        var at = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var draft = await calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Occurrence,
            "{\"kind\":\"explicit-instants\",\"instants\":[\"" + at + "Z\"]}",
            0,
            ct: ct
        );
        Applied(draft.Action, "calendar draft");
        Applied((await calendars.PublishAsync(calendar, draft.Version!.Value, ct: ct)).Action, "calendar publish");
    }

    /// <summary>Another namespace in this spec's schema, served by its own worker and filled with work of every kind.</summary>
    private async Task<HeldNamespace> HeldNamespaceAsync(CancellationToken ct)
    {
        if (Services.GetRequiredService<ISqlDialect>().Provider == DbProvider.Sqlite)
        {
            Assert.Skip("SQLite runs one writer at a time, so a transaction holding another namespace holds the whole file.");
        }

        var jobNamespace = Unique("other");
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobsManifest>(jobNamespace, ownerTeam: "test", description: GetType().FullName + ":other");
        });
        services.Configure<JobsOptions>(o => o.RunSystemJobs = false);
        var provider = services.BuildServiceProvider(validateScopes: true);
        var runtime = provider.GetServices<WorkerRuntime>().Single();
        await CatalogTestOps.DeployAndStartAsync(provider, runtime, ct);
        await FillAsync(provider, runtime, jobNamespace, ct);
        return new HeldNamespace(this, provider, runtime.RegisteredNamespaceIds[jobNamespace]);
    }

    /// <summary>
    /// Work of every kind in <paramref name="jobNamespace"/>: finished, failed, ready, laned, waiting on a signal and on a
    /// sleep, with steps, children, notes, limits, tags, an alert, and a calendar.
    /// </summary>
    private static async Task FillAsync(IServiceProvider services, WorkerRuntime runtime, string jobNamespace, CancellationToken ct)
    {
        var jobs = services.GetRequiredService<IJobs>();
        EnqueueJobRequest Of(string jobName, object? input = null) => new(jobNamespace, jobName, JobPayload.Json(input ?? new { }));
        async Task<EnqueueJobResponse> RunAsync(EnqueueJobRequest request)
        {
            var job = await jobs.EnqueueAsync(request, ct);
            for (
                var tick = 0;
                tick < 10 && (await jobs.GetStatusAsync(job, ct))?.Status is not (JobStatusCode.Succeeded or JobStatusCode.Failed);
                tick++
            )
            {
                await runtime.RunOnceAsync(jobNamespace, ct);
            }
            return job;
        }

        var finished = await RunAsync(Of("add-numbers", new AddNumbers(1, 2)));
        await RunAsync(Of("on-terminal-probe"));
        await RunAsync(Of("job-parent-one"));
        await RunAsync(Of("job-note"));
        await RunAsync(Of("concurrency-limit-probe"));
        await RunAsync(Of("rate-limited-probe"));
        await runtime.RunOnceAsync(jobNamespace, (await jobs.EnqueueAsync(Of("job-wait-signal"), ct)).JobId, ct);
        await runtime.RunOnceAsync(jobNamespace, (await jobs.EnqueueAsync(Of("job-step-basic"), ct)).JobId, ct);
        await runtime.RunOnceAsync(jobNamespace, (await jobs.EnqueueAsync(Of("job-sleep-basic"), ct)).JobId, ct);
        // Left Ready, after every run above so none of them claims these.
        await jobs.EnqueueAsync(Of("add-numbers", new AddNumbers(3, 4)), ct);
        await jobs.EnqueueAsync(Of("add-numbers", new AddNumbers(5, 6)) with { DeduplicationKey = "held" }, ct);
        await jobs.EnqueueAsync(Of("lane-step", new LaneStep("held", "one")) with { Lane = "held" }, ct);
        await jobs.EnqueueAsync(Of("lane-step", new LaneStep("held", "two")) with { Lane = "held" }, ct);
        await RaiseAlertAsync(services, jobNamespace, finished.JobId, ct);
        await TagAsync(services, jobNamespace, finished, ct);
        await PublishCalendarAsync(services, new CalendarLookup(jobNamespace, "held"), ct);
    }

    /// <summary>
    /// The other namespace's rows held by an open transaction from <see cref="HoldAsync"/> to <see cref="ReleaseAsync"/>,
    /// and each operation <see cref="Check"/> found waiting on them.
    /// </summary>
    private sealed class HeldNamespace(NamespaceIsolationSpec spec, ServiceProvider services, int namespaceId) : IAsyncDisposable
    {
        private readonly List<string> _findings = [];
        private DbConnection? _holder;
        private DbTransaction? _hold;
        private int _session;

        public async Task HoldAsync(CancellationToken ct)
        {
            _holder = await spec.Db.OpenConnectionAsync(ct);
            _hold = await _holder.BeginTransactionAsync(ct);
            Assert.True(await spec.HoldRowsAsync(_holder, _hold, namespaceId, ct) > 0, "nothing was held");
            _session = await spec.SessionAsync(_holder, _hold, ct);
        }

        public async Task Check(string name, Func<CancellationToken, Task> operation)
        {
            var ct = TestContext.Current.CancellationToken;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var run = operation(limit.Token);
            if (await Task.WhenAny(run, Task.Delay(Limit, ct)) == run)
            {
                await run;
                return;
            }
            // Still running: name what waits on the holder before the deadline cancels it and releases its locks.
            var blocked = await spec.BlockedAsync(_session, ct);
            limit.Cancel();
            var stopped = await Task.WhenAny(run, Task.Delay(Limit, ct)) == run;
            _findings.Add(
                $"{name}: waited past {Limit.TotalSeconds:0} s on the other namespace's rows"
                    + (stopped ? "" : ", and did not stop when cancelled")
                    + string.Concat(blocked.Select(b => "\n    " + b))
            );
        }

        /// <summary>Rolls the hold back and fails on every operation that waited.</summary>
        public async Task ReleaseAsync(CancellationToken ct)
        {
            await _hold!.RollbackAsync(ct);
            Assert.True(_findings.Count == 0, "Operations that waited on another namespace:\n" + string.Join("\n", _findings));
        }

        public async ValueTask DisposeAsync()
        {
            if (_hold is not null)
            {
                await _hold.DisposeAsync();
            }
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
            await services.DisposeAsync();
        }
    }

    private bool OnSqlServer => Services.GetRequiredService<ISqlDialect>().Provider == DbProvider.SqlServer;

    /// <summary>The holder's server session: SQL Server's session id or PostgreSQL's backend pid.</summary>
    private async Task<int> SessionAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = OnSqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Each statement the holder blocks, with the lock it waits for: what to make seek.</summary>
    private async Task<IReadOnlyList<string>> BlockedAsync(int holderSession, CancellationToken ct)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = OnSqlServer
            ? "SELECT CONCAT(r.wait_type, N' ', r.wait_resource, N': ', SUBSTRING(t.text, r.statement_start_offset / 2 + 1, "
                + "(CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1)) "
                + $"FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.blocking_session_id = {holderSession}"
            : $"SELECT wait_event_type || ' ' || coalesce(wait_event, '') || ': ' || query FROM pg_stat_activity WHERE {holderSession} = ANY (pg_blocking_pids(pid))";
        List<string> blocked = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var text = string.Join(' ', reader.GetString(0).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            blocked.Add(text.Length > 400 ? text[..400] + "..." : text);
        }
        return blocked;
    }

    /// <summary>
    /// Takes an exclusive lock on every row of <paramref name="namespaceId"/>: each table with a namespace column by it,
    /// each other table with a job column by the namespace's jobs, and the tags on the namespace, its definitions, and its
    /// jobs. Returns how many rows it holds.
    /// </summary>
    private async Task<int> HoldRowsAsync(DbConnection connection, DbTransaction transaction, int namespaceId, CancellationToken ct)
    {
        var schema = Schema.SchemaName;
        var columns = new List<(string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText =
                "SELECT c.table_name, c.column_name FROM information_schema.columns c "
                + "JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name "
                + $"WHERE c.table_schema = '{schema}' AND t.table_type = 'BASE TABLE' AND c.column_name IN ('namespace_id', 'job_id')";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // Row locks only: a page or table lock would cover the test namespace's rows too and fail a statement that seeks.
        string Target(string table) => OnSqlServer ? $"{schema}.{table} WITH (ROWLOCK)" : $"{schema}.{table}";
        var ofNamespace = $"(SELECT j.job_id FROM {schema}.jobs j WHERE j.namespace_id = {namespaceId})";
        List<string> updates = [];
        foreach (var table in columns.Select(c => c.Table).Distinct())
        {
            var byNamespace = columns.Contains((table, "namespace_id"));
            var set = await WritableColumnAsync(connection, transaction, table, byNamespace ? "namespace_id" : "job_id", ct);
            updates.Add(
                byNamespace
                    ? $"UPDATE {Target(table)} SET {set} = {set} WHERE namespace_id = {namespaceId}"
                    : $"UPDATE {Target(table)} SET {set} = {set} WHERE job_id IN {ofNamespace}"
            );
        }
        updates.Add(
            $"UPDATE {Target("tags")} SET tag_value = tag_value WHERE (entity_code = 80 /* EntityCode.Namespace */ AND entity_id = {namespaceId})"
                + $" OR (entity_code = 30 /* EntityCode.Definition */ AND entity_id IN (SELECT d.definition_id FROM {schema}.definitions d WHERE d.namespace_id = {namespaceId}))"
                + $" OR (entity_code = 50 /* EntityCode.Job */ AND entity_id IN {ofNamespace})"
        );

        var held = 0;
        foreach (var text in updates)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = text;
            held += await update.ExecuteNonQueryAsync(ct);
        }
        return held;
    }

    /// <summary>
    /// A column an update may set to itself: <paramref name="fallback"/> on PostgreSQL, and on SQL Server, which refuses
    /// to update an identity column even to its own value, the table's first column that is not identity, computed, or
    /// rowversion.
    /// </summary>
    private async Task<string> WritableColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string fallback,
        CancellationToken ct
    )
    {
        if (!OnSqlServer)
        {
            return fallback;
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT TOP 1 c.name FROM sys.columns c "
            + $"WHERE c.object_id = OBJECT_ID(N'{Schema.SchemaName}.{table}') AND c.is_identity = 0 AND c.is_computed = 0 AND c.system_type_id <> 189 "
            + "ORDER BY c.column_id";
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
}
