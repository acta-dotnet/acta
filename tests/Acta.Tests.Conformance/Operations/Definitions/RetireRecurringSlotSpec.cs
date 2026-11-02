using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// The retire verb on a recurring job: the slot is left inert instead of cancelled, and a newer build that registers
/// the definition again retimes its schedules from its start, on the same slot.
/// </summary>
[ConformanceSpec(
    "definitions.retire-recurring",
    "Retire leaves a recurring slot inert until a newer build resumes it",
    Area = "Catalog",
    Contract = "Retiring a recurring definition settles its queued firing, leaves the slot Idle or as the operator held it, and a newer build resumes its schedules from now."
)]
public sealed class RetireRecurringSlotSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private const string Actor = "spec-operator";

    private const string Reason = "the handler left the build";

    private static readonly DateTime Generation = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime OlderGeneration = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        // Pinned so a build can start on either side of the stored generation.
        services.Configure<JobsOptions>(o => o.ManifestGenerationUtc = Generation);
    }

    [Fact(DisplayName = "A recurring slot retired mid-occurrence returns to Idle, where its retired definition leaves it inert")]
    public async Task A_recurring_slot_retired_mid_occurrence_returns_to_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        RecurringPingHandler.DuringNextFire[TestNamespace] = async () =>
        {
            Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
            await RunAnsweredHeartbeatAsync(ct);
        };

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        Assert.Null(slot.RetentionUntilUtc);
    }

    [Fact(DisplayName = "A retired definition's schedules keep no cursor, which takes them out of the upcoming-occurrence index")]
    public async Task A_retired_definitions_schedules_keep_no_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        Assert.All(await ScheduleTestOps.ReadAsync(Db, slotId, ct), s => Assert.NotNull(s.NextOccurrenceAtUtc));

        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);

        var schedules = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        Assert.NotEmpty(schedules);
        Assert.All(
            schedules,
            s =>
            {
                Assert.Null(s.NextOccurrenceAtUtc);
            }
        );
    }

    [Fact(DisplayName = "A newer build that registers a retired definition again resumes its schedules from now on the same Idle slot")]
    public async Task A_reactivated_definition_resumes_its_schedules_from_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.All(
            await ScheduleTestOps.ReadAsync(Db, slotId, ct),
            s =>
            {
                Assert.Null(s.NextOccurrenceAtUtc);
                Assert.Equal("retired", s.Config.TimingFingerprint);
            }
        );

        var comeback = DateTime.UtcNow;
        await StartNewerBuildAsync("comeback", ct);

        Assert.Equal(DefinitionStatusCode.Active, (await Operations.Definitions.GetAsync(TestNamespace, "recurring-ping", ct))!.Status);
        Assert.Equal(slotId, await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        var resumed = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        Assert.All(
            resumed,
            s =>
            {
                Assert.NotEqual("retired", s.Config.TimingFingerprint);
                Assert.Equal(EvaluationStatusCode.Scheduled, s.EvaluationStatus);
                Assert.InRange(s.NextOccurrenceAtUtc!.Value, comeback.AddSeconds(-5), comeback.AddMinutes(6));
            }
        );

        // Starting it again changes nothing, and the resumed schedules admit one firing on the slot.
        await StartNewerBuildAsync("comeback-again", ct);
        Assert.Equal(resumed.Select(s => s.Version), (await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Select(s => s.Version));
        await MakeDueAsync(slotId, ct);
        Assert.Equal(1, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(0, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
    }

    [Fact(DisplayName = "A retire settles a queued firing and leaves an operator's pause or cancel on the slot through the comeback")]
    public async Task A_retire_settles_a_queued_firing_and_keeps_operator_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);

        // A firing admitted and not yet claimed settles Cancelled, and the slot goes back to Idle.
        await MakeDueAsync(slotId, ct);
        Assert.Equal(1, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        var envelope = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.Equal(CheckpointStatusCode.Cancelled, envelope!.Status);

        // An operator's pause holds across a retire and a comeback, and so does a cancel.
        await StartNewerBuildAsync("comeback", ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(slotId, ct)).Status);
        await StartNewerBuildAsync("comeback-paused", ct);
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(slotId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
        await StartNewerBuildAsync("comeback-cancelled", ct);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "An older build that starts while the definition is retired leaves its schedules as the retire left them")]
    public async Task An_older_build_leaves_a_retired_definitions_schedules_inert()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
        var retired = await ScheduleTestOps.ReadAsync(Db, slotId, ct);

        await StartBuildAsync(OlderGeneration, "older", ct);

        Assert.Equal(DefinitionStatusCode.Retired, (await Operations.Definitions.GetAsync(TestNamespace, "recurring-ping", ct))!.Status);
        var after = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        Assert.Equal(retired.Select(s => s.Version), after.Select(s => s.Version));
        Assert.All(
            after,
            s =>
            {
                Assert.Equal("retired", s.Config.TimingFingerprint);
                Assert.Null(s.NextOccurrenceAtUtc);
            }
        );
        Assert.Equal(0, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
    }

    [Fact(DisplayName = "A retired definition's slot refuses a manual firing until a build that carries the definition starts")]
    public async Task A_retired_definitions_slot_refuses_a_manual_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);

        Assert.Equal(ControlAction.Rejected, (await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.JobRescheduled, ct));
        Assert.Equal(
            0,
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing && c.Status == CheckpointStatusCode.Pending)
                .CountAsync(ct)
        );

        await StartNewerBuildAsync("comeback", ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(
        DisplayName = "A firing that fails into a retry after the retire lands Cancelled with its firing settled, and a restart returns the slot to Idle"
    )]
    public async Task A_firing_retrying_at_the_retire_is_cancelled_and_a_restart_revives_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 1;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        await MakeDueAsync(slotId, ct);
        Assert.Equal(1, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
        RecurringPingHandler.DuringNextFire[TestNamespace] = async () =>
        {
            Assert.Equal(ControlAction.Applied, (await RetireAsync("recurring-ping", ct)).Action);
            await RunAnsweredHeartbeatAsync(ct);
        };

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(EventReason.DefinitionRetired, EventDetail(await ReadSingleEventAsync(slotId, EventCode.JobCancelled, ct), "reason"));
        var envelope = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.Equal(CheckpointStatusCode.Cancelled, envelope!.Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
    }

    /// <summary>A newer build's worker starting in the test namespace: it registers the definitions and their schedules.</summary>
    private Task StartNewerBuildAsync(string tag, CancellationToken ct) => StartBuildAsync(Generation, tag, ct);

    private async Task StartBuildAsync(DateTime generation, string tag, CancellationToken ct)
    {
        await using var build = BuildGenerationProvider<TestJobsManifest>(generation, tag);
        await build.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);
    }

    /// <summary>
    /// Puts every schedule of the slot one second past due on the database clock, which the admitter reads, inside the
    /// misfire grace.
    /// </summary>
    private async Task MakeDueAsync(long slotId, CancellationToken ct)
    {
        var due = (await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct)).AddSeconds(-1);
        foreach (var s in await ScheduleTestOps.ReadAsync(Db, slotId, ct))
        {
            await Db.From<ScheduleStateEntity>()
                .Where(st => st.Id == s.Config.Id)
                .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = due, NextEvaluationAtUtc = due }, ct);
        }
    }

    private async Task<RetireDefinitionResponse> RetireAsync(string jobName, CancellationToken ct)
    {
        var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);
        Assert.NotNull(definition);
        return await Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, Reason, Actor, ct);
    }
}
