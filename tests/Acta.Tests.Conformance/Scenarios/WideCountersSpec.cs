using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A job row's execution number and version are 64-bit. Each fact plants both past 2^31 on a job and drives one path
/// that carries them, then reads the stored counters back: each moved by exactly what the path moves it, and neither
/// wrapped or failed on the way.
/// </summary>
[ConformanceSpec(
    "jobs.wide-counters",
    "A job past 32-bit counters runs and is controlled like any other",
    Area = "Execution",
    Contract = "A job whose execution number and version are past 32 bits is claimed, run, controlled and reclaimed like any other, and its parent reads its outcome."
)]
public sealed class WideCountersSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private const long PlantedExecution = 3_000_000_000;
    private const long PlantedVersion = 5_000_000_000;

    // Twelve digits: a child's execution number travels as text in its outcome, and ten digits fit a 32-bit width's text.
    private const long PlantedChildExecution = 600_000_000_000;
    private const int LeaseTtlSeconds = 300;

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A job past 32-bit counters is claimed and lands, and its events carry the wide execution number")]
    public async Task Claim_and_land()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await JobStateStaging.SetEventRecordingAsync(Db, job.JobId, EventRecordingCode.Audit, ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));

        // The claim moves both counters and the landing moves the version.
        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);
        var attempt = await AttemptEventsAsync(job.JobId, ct);
        Assert.Contains(attempt, e => e.EventCode == EventCode.JobExecutionSucceeded);
        Assert.All(attempt, e => Assert.Equal(PlantedExecution + 1, e.ExecutionNumber));
    }

    [Fact(DisplayName = "The worker's batch claim takes a job past 32-bit counters and lands it")]
    public async Task Batch_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);

        // The namespace tick claims through claim_jobs, the worker's own path; a claim by job id takes another statement.
        await TickUntilAsync(async () => (await ReadStateAsync(job.JobId, ct)).Status == JobStatusCode.Succeeded, ct);

        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);
    }

    [Fact(DisplayName = "A heartbeat renews the lease of an attempt past 32 bits and leaves the version where the claim left it")]
    public async Task Lease_renewal()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct);
        var claimed = Assert.Single(await Execution.ClaimJobAsync(NamespaceId, worker, LeaseTtlSeconds, job, ct));
        Assert.Equal(PlantedExecution + 1, claimed.ExecutionNumber);

        var beat = await Services
            .GetRequiredService<WorkersOperations>()
            .ExtendWorkerLeasesAsync(
                new ExtendWorkerLeasesCommand(worker, LeaseTtlSeconds, false, [(job.JobId, claimed.ExecutionNumber)], []),
                ct
            );

        var lease = Assert.Single(beat.Leases);
        Assert.True(lease.Renewed);
        Assert.Equal(PlantedExecution + 1, lease.ExecutionNumber);
        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 1, ct);
    }

    [Theory(DisplayName = "A fenced handler write lands under an execution number past 32 bits")]
    [InlineData("job-step-basic")]
    [InlineData("job-note")]
    [InlineData("job-sleep-basic")]
    [InlineData("job-step-reset-inside")]
    public async Task Fenced_write(string jobName)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.None), ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);

        var run = await Runtime.RunOnceAsync(job, ct);

        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);
        switch (jobName)
        {
            case "job-step-basic":
                Assert.Equal(RunOnceStatus.Completed, run);
                var step = Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == job.JobId).ToListAsync(ct));
                Assert.Equal(StepStatusCode.Succeeded, step.Status);
                break;
            case "job-note":
                Assert.Equal(RunOnceStatus.Completed, run);
                var notes = await Db.From<EventEntity>()
                    .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == job.JobId && e.EventCode == EventCode.JobNoteRecorded)
                    .ToListAsync(ct);
                Assert.Equal(2, notes.Count);
                Assert.All(notes, n => Assert.Equal(PlantedExecution + 1, n.ExecutionNumber));
                break;
            case "job-step-reset-inside":
                // The reset's own statement takes the wide execution number as its fence and stamps its event with it.
                Assert.Equal(RunOnceStatus.Failed, run);
                var reset = Assert.Single(
                    await Db.From<EventEntity>()
                        .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == job.JobId && e.EventCode == EventCode.JobStateReset)
                        .ToListAsync(ct)
                );
                Assert.Equal(PlantedExecution + 1, reset.ExecutionNumber);
                break;
            default:
                Assert.Equal(RunOnceStatus.Rearmed, run);
                Assert.Equal(1, await CountVariableAsync(job.JobId, "ran.before", ct));
                var timer = Assert.Single(
                    await Db.From<CheckpointEntity>().Where(c => c.JobId == job.JobId && c.Kind == CheckpointKindCode.Timer).ToListAsync(ct)
                );
                Assert.Equal("nap", timer.CheckpointName);
                break;
        }
    }

    [Fact(DisplayName = "A parent past 32-bit counters cancels its timed-out child under its own fence")]
    public async Task Fenced_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-try-wait-child", JobPayload.Json(new TryWaitChildStart("job-wait-signal"))),
            ct
        );
        await PlantAsync(parent.JobId, PlantedExecution, PlantedVersion, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct)).Id;

        // The wait's deadline passes: the parent resumes and cancels the child with its own attempt as the cancel's fence.
        await ChildTimeoutSpec.ExpireChildWaitAsync(Db, parent.JobId, child, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(JobStatusCode.Cancelled, (await ReadStateAsync(child, ct)).Status);
        Assert.Equal(PlantedExecution + 2, (await ReadStateAsync(parent.JobId, ct)).ExecutionNumber);
    }

    [Fact(DisplayName = "A signal wait past 32 bits suspends the job, and the raise releases it to run again")]
    public async Task Signal_wait_and_raise()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(job, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadStateAsync(job.JobId, ct)).Status);
        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(job, "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadStateAsync(job.JobId, ct)).Status);
        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 3, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
        Assert.Equal(1, await CountVariableAsync(job.JobId, "ran.after", ct));
        await AssertCountersAsync(job.JobId, PlantedExecution + 2, PlantedVersion + 5, ct);
    }

    [Fact(DisplayName = "A child started under a parent past 32 bits lands its wide execution in the outcome the parent reads")]
    public async Task Child_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        await PlantAsync(parent.JobId, PlantedExecution, PlantedVersion, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));
        await AssertCountersAsync(parent.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);

        // The child's own counters differ from the parent's, so the outcome's execution number names the child's attempt.
        await PlantAsync(child.Id, PlantedChildExecution, PlantedVersion * 2, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));
        await AssertCountersAsync(child.Id, PlantedChildExecution + 1, PlantedVersion * 2 + 2, ct);
        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        var outcome = ChildOutcomeEnvelope.Parse(latch.Value!);
        Assert.Equal(JobStatusCode.Succeeded, outcome.Status);
        Assert.Equal(PlantedChildExecution + 1, outcome.ExecutionNumber);
        await AssertCountersAsync(parent.JobId, PlantedExecution + 1, PlantedVersion + 3, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct))!.Doubled);
        await AssertCountersAsync(parent.JobId, PlantedExecution + 2, PlantedVersion + 5, ct);
    }

    [Fact(DisplayName = "Recovery reclaims an attempt past 32 bits whose lease lapsed and charges it to the wide execution")]
    public async Task Reclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await JobStateStaging.SetEventRecordingAsync(Db, job.JobId, EventRecordingCode.Audit, ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct);
        var lost = await ChaosSpecHelpers.StrandExecutingAsync(Services, NamespaceId, worker, job.JobId, ct);
        Assert.Equal(PlantedExecution + 1, lost.ExecutionNumber);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, NamespaceId, ct)).Reclaimed);

        var state = await ReadStateAsync(job.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, state.Status);
        Assert.Equal(1, state.FailureCount);
        await AssertCountersAsync(job.JobId, PlantedExecution + 1, PlantedVersion + 2, ct);
        var orphaned = await ReadSingleEventAsync(job.JobId, EventCode.JobExecutionOrphaned, ct);
        Assert.Equal(PlantedExecution + 1, orphaned.ExecutionNumber);
    }

    [Theory(DisplayName = "A control verb applies to a job past 32-bit counters, compared at an expected version past 32 bits")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("restart")]
    [InlineData("reschedule")]
    [InlineData("reprioritize")]
    [InlineData("cancel")]
    [InlineData("amend-input")]
    [InlineData("purge")]
    [InlineData("stale")]
    public async Task Control_verb(string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);

        switch (verb)
        {
            case "pause":
                AssertApplied(await Jobs.PauseAsync(job, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                break;
            case "resume":
                AssertApplied(await Jobs.PauseAsync(job, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                AssertApplied(await Jobs.ResumeAsync(job, expectedVersion: PlantedVersion + 1, ct: ct), PlantedVersion + 2);
                break;
            case "restart":
                AssertApplied(await Jobs.RestartAsync(job, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                break;
            case "reschedule":
                var later = DateTime.UtcNow.AddHours(1);
                AssertApplied(await Jobs.RescheduleAsync(job, later, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                break;
            case "reprioritize":
                var high = JobPriorityCode.High;
                AssertApplied(await Jobs.ReprioritizeAsync(job, high, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                break;
            case "cancel":
                AssertApplied(await Jobs.CancelAsync(job, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                break;
            case "amend-input":
                Assert.Equal(
                    ControlAction.Applied,
                    (await Jobs.UpdateInputAsync(job, JobPayload.Json(new AddNumbers(4, 5)), ct: ct)).Action
                );
                await AssertCountersAsync(job.JobId, PlantedExecution, PlantedVersion, ct);
                return;
            case "purge":
                AssertApplied(await Jobs.CancelAsync(job, expectedVersion: PlantedVersion, ct: ct), PlantedVersion + 1);
                Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(job, ct: ct)).Action);
                Assert.Equal(0, await Db.From<JobStateEntity>().Where(r => r.Id == job.JobId).CountAsync(ct));
                return;
            default:
                var conflict = await Jobs.PauseAsync(job, expectedVersion: PlantedVersion - 1, ct: ct);
                Assert.Equal(ControlAction.VersionConflict, conflict.Action);
                Assert.Equal(PlantedVersion, conflict.Version);
                break;
        }

        var versions = verb switch
        {
            "resume" => 2,
            "stale" => 0,
            _ => 1,
        };
        await AssertCountersAsync(job.JobId, PlantedExecution, PlantedVersion + versions, ct);
    }

    [Fact(DisplayName = "A permanent slot past 32-bit counters is admitted at its version, fires, and returns to Idle")]
    public async Task Slot_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, "recurring-ping", ct);
        await PlantAsync(slotId, PlantedExecution, PlantedVersion, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.schedules_state SET next_occurrence_at_utc = @p_due, next_evaluation_at_utc = @p_due "
                + "WHERE schedule_id IN (SELECT s.schedule_id FROM {schema}.schedules s WHERE s.job_id = @p_slot)",
            ct,
            ("@p_due", DateTime.UtcNow.AddSeconds(-1)),
            ("@p_slot", slotId)
        );

        // The admission compares the slot's version as the plan read it and moves it.
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadStateAsync(slotId, ct)).Status);
        await AssertCountersAsync(slotId, PlantedExecution, PlantedVersion + 1, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(JobStatusCode.Idle, (await ReadStateAsync(slotId, ct)).Status);
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));
        await AssertCountersAsync(slotId, PlantedExecution + 1, PlantedVersion + 3, ct);
    }

    [Theory(DisplayName = "A schedule verb applies to a schedule whose permanent job is past 32-bit counters")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("trigger")]
    public async Task Schedule_verb_on_a_worn_slot(string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        var schedules = Operations.Schedules;
        var schedule = new ScheduleLookup(TestNamespace, "recurring-ping", "every-5-minutes");
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, "recurring-ping", ct);
        await PlantAsync(slotId, PlantedExecution, PlantedVersion, ct);

        // Each verb's statement reads the slot's execution number for the event it writes.
        var action = verb switch
        {
            "pause" => (await schedules.PauseAsync(schedule, ct: ct)).Action,
            "resume" => (await schedules.PauseAsync(schedule, ct: ct)).Action == ControlAction.Applied
                ? (await schedules.ResumeAsync(schedule, ct: ct)).Action
                : ControlAction.Rejected,
            _ => (await schedules.TriggerAsync(schedule, ct: ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        Assert.Equal(PlantedExecution, (await ReadStateAsync(slotId, ct)).ExecutionNumber);
    }

    [Fact(DisplayName = "Retiring a definition cancels its job past 32-bit counters and records the wide execution number")]
    public async Task Retire_cancels()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);
        var definition = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        Assert.NotNull(definition);

        var retired = await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", definition.Version, ct: ct);

        Assert.Equal(ControlAction.Applied, retired.Action);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadStateAsync(job.JobId, ct)).Status);
        await AssertCountersAsync(job.JobId, PlantedExecution, PlantedVersion + 1, ct);
        Assert.Equal(PlantedExecution, (await ReadSingleEventAsync(job.JobId, EventCode.JobCancelled, ct)).ExecutionNumber);
    }

    [Theory(DisplayName = "A verb on a 32-bit version answers an expected version past 32 bits as a conflict and changes nothing")]
    [InlineData("definition")]
    [InlineData("overrides")]
    [InlineData("namespace")]
    [InlineData("tenant")]
    [InlineData("calendar")]
    public async Task Narrow_version_conflict(string entity)
    {
        var ct = TestContext.Current.CancellationToken;
        switch (entity)
        {
            case "definition":
                var definition = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
                var retired = await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", PlantedVersion, ct: ct);
                Assert.Equal((ControlAction.VersionConflict, definition!.Version), (retired.Action, retired.Version));
                Assert.Equal(
                    DefinitionStatusCode.Active,
                    (await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct))!.Status
                );
                break;
            case "overrides":
                var current = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
                var overridden = await Operations.Definitions.UpdateOverridesAsync(
                    TestNamespace,
                    "add-numbers",
                    PlantedVersion,
                    new DefinitionPolicyOverrides(MaxAttempts: 7),
                    ct: ct
                );
                Assert.Equal((ControlAction.VersionConflict, current!.Version), (overridden.Action, overridden.Version));
                break;
            case "tenant":
                var tenantName = Unique("wide-tenant");
                await Operations.Tenants.RegisterAsync(tenantName, ct: ct);
                var renamed = await Operations.Tenants.UpdateAsync(tenantName, PlantedVersion, "Other Name", null, ct: ct);
                Assert.Equal(ControlAction.VersionConflict, renamed.Action);
                break;
            case "namespace":
                var before = await Operations.Namespaces.GetAsync(TestNamespace, ct);
                var updated = await Operations.Namespaces.UpdateAsync(TestNamespace, PlantedVersion, "other-team", null, ct: ct);
                Assert.Equal((ControlAction.VersionConflict, before!.Version), (updated.Action, updated.Version));
                Assert.Equal(before.OwnerTeam, (await Operations.Namespaces.GetAsync(TestNamespace, ct))!.OwnerTeam);
                break;
            default:
                var calendar = new CalendarLookup(TestNamespace, "wide-counters");
                var saved = await Operations.Calendars.SaveDraftAsync(
                    calendar,
                    CalendarRoleCode.Availability,
                    CalendarTestOps.AvailabilityDefinition,
                    PlantedVersion,
                    ct: ct
                );
                Assert.Equal(ControlAction.VersionConflict, saved.Action);
                Assert.Contains($"not {PlantedVersion}", Assert.Single(saved.Problems), StringComparison.Ordinal);
                Assert.Null(await Operations.Calendars.GetAsync(calendar, ct: ct));
                break;
        }
    }

    [Theory(DisplayName = "A calendar version past 32 bits is no version of the family, and nothing is changed for it")]
    [InlineData("read")]
    [InlineData("preview")]
    [InlineData("publish")]
    [InlineData("publish-expected")]
    [InlineData("archive")]
    public async Task Wide_calendar_version(string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        var calendars = Operations.Calendars;
        var calendar = new CalendarLookup(TestNamespace, "wide-versions");
        var first = await calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, first.Action);
        Assert.Equal(ControlAction.Applied, (await calendars.PublishAsync(calendar, first.Version!.Value, ct: ct)).Action);
        // Cut to 32 bits, this is the version just published: a narrowed carrier would find the family's own version.
        var wide = (1L << 32) + first.Version.Value;

        switch (verb)
        {
            case "read":
                Assert.Null(await calendars.GetAsync(calendar, wide, ct));
                break;
            case "preview":
                Assert.Null(await calendars.PreviewAsync(calendar, wide, ct: ct));
                break;
            case "publish":
                Assert.Equal(ControlAction.NotFound, (await calendars.PublishAsync(calendar, wide, ct: ct)).Action);
                break;
            case "publish-expected":
                // A real candidate, published against a current version nobody saw.
                var revised = CalendarTestOps.Revised(CalendarTestOps.AvailabilityDefinition, 2);
                var second = await calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, revised, ct: ct);
                Assert.Equal(ControlAction.Applied, second.Action);
                Assert.Equal(
                    ControlAction.VersionConflict,
                    (await calendars.PublishAsync(calendar, second.Version!.Value, wide, ct: ct)).Action
                );
                break;
            default:
                Assert.Equal(ControlAction.Rejected, (await calendars.ArchiveAsync(calendar, wide, ct: ct)).Action);
                break;
        }

        // The family's current version is the one published above, whatever the verb.
        Assert.Equal(first.Version, (await calendars.GetAsync(calendar, ct: ct))!.CurrentVersion);
    }

    [Fact(DisplayName = "The job read, the job list, explain, and the event list answer a job past 32-bit counters")]
    public async Task Reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAddNumbersAsync(ct);
        await JobStateStaging.SetEventRecordingAsync(Db, job.JobId, EventRecordingCode.Audit, ct);
        await PlantAsync(job.JobId, PlantedExecution, PlantedVersion, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));

        var read = await Jobs.GetAsync(job, ct);
        Assert.NotNull(read);
        Assert.Equal(PlantedExecution + 1, read.ExecutionNumber);
        Assert.Equal(PlantedVersion + 2, read.Version);

        var listed = await Operations.Ledger.ListJobsAsync(new ListJobsQuery(TestNamespace, PageSize: 100), ct);
        Assert.Equal(PlantedExecution + 1, Assert.Single(listed.Items, j => j.JobId == job.JobId).ExecutionNumber);

        var explained = await Jobs.ExplainAsync(job, ct);
        Assert.NotNull(explained);
        Assert.Equal(JobStatusCode.Succeeded, explained.Status);

        var events = await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: job.JobId, PageSize: 100), ct);
        Assert.Contains(events.Items, e => e.EventCode == EventCode.JobExecutionSucceeded && e.ExecutionNumber == PlantedExecution + 1);
    }

    private static void AssertApplied(IControlResponse response, long version)
    {
        Assert.Equal(ControlAction.Applied, response.Action);
        Assert.Equal(version, response.Version);
    }

    private Task<EnqueueJobResponse> EnqueueAddNumbersAsync(CancellationToken ct) =>
        Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct).AsTask();

    private async Task PlantAsync(long jobId, long executionNumber, long version, CancellationToken ct) =>
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET execution_number = @p_execution, version = @p_version WHERE job_id = @p_id",
                ct,
                ("@p_execution", executionNumber),
                ("@p_version", version),
                ("@p_id", jobId)
            )
        );

    private async Task AssertCountersAsync(long jobId, long executionNumber, long version, CancellationToken ct)
    {
        var state = await ReadStateAsync(jobId, ct);
        Assert.Equal(executionNumber, state.ExecutionNumber);
        Assert.Equal(version, state.Version);
    }

    private async Task<JobStateEntity> ReadStateAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<JobStateEntity>().Where(r => r.Id == jobId).ToListAsync(ct));

    private async Task<IReadOnlyList<EventEntity>> AttemptEventsAsync(long jobId, CancellationToken ct) =>
        (await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct))
            .Where(e => e.ExecutionNumber is not null)
            .ToList();
}
