using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>
/// The admission loop of one worker namespace, driven one pass at a time: a due schedule's firing is admitted onto
/// its Idle slot with the envelope that names it, a pass with nothing due reports the horizon it may sleep until, an
/// outstanding firing fills the slot so later occurrences wait, and settlement wakes what waited.
/// </summary>
[ConformanceSpec(
    "schedule.admitter",
    "A pass admits due firings, fills the slot, and sleeps until the horizon",
    Area = "Scheduling",
    Contract = "A pass admits a due firing onto its Idle slot, reports the earliest next evaluation, and later occurrences wait on the outstanding firing until settlement."
)]
[Deploys("recurring-ping")]
public sealed class ScheduleAdmitterSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ScheduleAdmitterSpec>>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A pass admits a due schedule's firing: the slot is Ready with the envelope naming it and the cursor advanced")]
    public async Task A_pass_admits_a_due_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);

        Clock.AdvanceTo(occurrence);
        var pass = await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.True(pass.Admitted >= 1, "the pass admitted nothing");
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.NotNull(slot.NextRunAtUtc);
        Assert.Equal(0, slot.FailureCount);

        var envelope = await EnvelopeAsync(slotId, ct);
        Assert.Equal(FiringEnvelope.ScheduledKind, envelope.Kind);
        var contributor = Assert.Single(envelope.Contributors);
        Assert.Equal(ScheduleName, contributor.ScheduleName);
        Assert.Equal(occurrence, contributor.OccurrenceAtUtc);

        var schedule = await ScheduleAsync(slotId, ct);
        Assert.Equal(occurrence.AddMinutes(5), schedule.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, schedule.EvaluationStatus);
    }

    [Fact(DisplayName = "A pass with nothing due admits nothing and reports the earliest next evaluation as its horizon")]
    public async Task A_pass_with_nothing_due_reports_the_horizon()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);

        // The clock sits before every first occurrence the registration seeded.
        var pass = await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(0, pass.Admitted);
        Assert.Equal(0, pass.Stale);
        Assert.NotNull(pass.NextWakeUtc);
        Assert.True(pass.NextWakeUtc <= occurrence, "the horizon lies past a schedule's next evaluation");
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "An outstanding firing fills the slot: a later occurrence waits for capacity until settlement wakes it")]
    public async Task A_later_occurrence_waits_until_the_firing_settles()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);
        var first = await ScheduleCursorAsync(slotId, ct);

        Clock.AdvanceTo(first);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);

        // The next occurrence comes due while the first firing is still outstanding: the member parks on the slot.
        Clock.AdvanceTo(first.AddMinutes(5));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        var waiting = await ScheduleAsync(slotId, ct);
        Assert.Equal(ScheduleEvaluationStatusCode.WaitingForCapacity, waiting.EvaluationStatus);
        Assert.Null(waiting.NextEvaluationAtUtc);
        Assert.Equal(first.AddMinutes(5), waiting.NextOccurrenceAtUtc);

        // The attempt settles the firing, which wakes the member; the next pass admits the waiting occurrence.
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        var woken = await ScheduleAsync(slotId, ct);
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, woken.EvaluationStatus);
        Assert.NotNull(woken.NextEvaluationAtUtc);

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(first.AddMinutes(5), Assert.Single((await EnvelopeAsync(slotId, ct)).Contributors).OccurrenceAtUtc);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(2, RecurringPingHandler.TriggersFor(TestNamespace).Count);
    }

    [Fact(DisplayName = "A pass reports its decision, lag, overdue frontier, and wall time on the meter")]
    public async Task A_pass_reports_itself_on_the_meter()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);
        // Each host owns its meter, so the listener hears this spec's measurements only.
        var meter = Services.GetRequiredService<ActaMetrics>().Meter;
        var seen = new ConcurrentBag<(string Instrument, string? Decision, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        static string? Decision(ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            tags.ToArray().FirstOrDefault(t => t.Key == "decision").Value as string;
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) => seen.Add((i.Name, Decision(tags), value)));
        listener.SetMeasurementEventCallback<double>((i, value, tags, _) => seen.Add((i.Name, Decision(tags), value)));
        listener.Start();

        // Three seconds late, inside the grace: the pass admits the occurrence and measures how late it was.
        Clock.AdvanceTo(occurrence.AddSeconds(3));
        Assert.True((await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted >= 1, "the pass admitted nothing");

        Assert.Contains(("acta.schedule.decisions", "admitted", 1d), seen);
        Assert.Contains(("acta.schedule.lag", null, 3000d), seen);
        Assert.Contains(("acta.schedule.due.age", null, 3d), seen);
        Assert.Contains(seen, m => m.Instrument == "acta.schedule.pass" && m.Value >= 0);
    }

    [Fact(DisplayName = "A schedule whose evaluation starts failing records it once, below the Audit level too")]
    public async Task A_failing_evaluation_is_recorded_once_at_every_level()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);
        // A level where a schedule's decisions leave no event, and a zone no host knows, so the evaluation throws.
        await Db.ExecuteRawAsync(
            $"UPDATE {{schema}}.jobs SET event_recording_code = {(int)EventRecordingCode.Failures} WHERE job_id = @p_id",
            ct,
            ("@p_id", slotId)
        );
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.schedules SET time_zone_id = 'Nowhere/Unknown' WHERE job_id = @p_id",
            ct,
            ("@p_id", slotId)
        );

        Clock.AdvanceTo(occurrence);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        // The retry a minute on meets the same error, on a slot that has become busy, and adds nothing: a failing
        // schedule is not parked as waiting for the slot.
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        var retry = occurrence.AddMinutes(1).AddSeconds(1);
        Clock.AdvanceTo(retry);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        var schedule = await ScheduleAsync(slotId, ct);
        Assert.Equal(ScheduleEvaluationStatusCode.EvaluationError, schedule.EvaluationStatus);
        Assert.Equal(retry.AddMinutes(1), schedule.NextEvaluationAtUtc);
        var failed = Assert.Single(
            await Db.From<EventEntity>()
                .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == slotId && e.EventCode == EventCode.ScheduleEvaluationFailed)
                .ToListAsync(ct)
        );
        Assert.Contains("Nowhere/Unknown", failed.EventMessage);
        Assert.Equal($"{{\"schedule\":\"{ScheduleName}\"}}", System.Text.Encoding.UTF8.GetString(failed.Details!));
    }

    // A newer build can store a code this one cannot read while both serve the namespace.
    [Theory(DisplayName = "A schedule that holds a code this build does not know is left alone: not due, no wake, no write")]
    [InlineData("schedule_status_code = 250")]
    [InlineData("expression_kind_code = 250")]
    [InlineData("misfire_strategy_code = 250")]
    [InlineData("full_behavior_code = 250")]
    [InlineData("expire_action_code = 250, expire_after_seconds = 60")]
    public async Task A_schedule_this_build_cannot_read_is_left_for_one_that_can(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);
        await Db.ExecuteRawAsync($"UPDATE {{schema}}.schedules SET {unknown} WHERE job_id = @p_id", ct, ("@p_id", slotId));
        var before = await ScheduleAsync(slotId, ct);

        Clock.AdvanceTo(occurrence.AddMinutes(1));
        var pass = await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        var after = await ScheduleAsync(slotId, ct);
        Assert.Equal(occurrence, after.NextOccurrenceAtUtc);
        Assert.Equal(before.Cursor.Version, after.Cursor.Version);
        Assert.Equal(before.Version, after.Version);
        // Its overdue wake is not the namespace's horizon, so the admitter does not poll on a row it will never move.
        Assert.True(pass.NextWakeUtc is null || pass.NextWakeUtc > before.NextEvaluationAtUtc, $"horizon {pass.NextWakeUtc:O}");
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private async Task<ScheduleRow> ScheduleAsync(long slotId, CancellationToken ct) =>
        Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var schedule = await ScheduleAsync(slotId, ct);
        Assert.NotNull(schedule.NextOccurrenceAtUtc);
        return schedule.NextOccurrenceAtUtc!.Value;
    }

    private async Task<FiringEnvelope> EnvelopeAsync(long slotId, CancellationToken ct)
    {
        var firing = Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).ToListAsync(ct)
        );
        Assert.Equal(CheckpointStatusCode.Pending, firing.Status);
        var envelope = FiringEnvelope.FromJson(firing.Value);
        Assert.NotNull(envelope);
        return envelope;
    }
}
