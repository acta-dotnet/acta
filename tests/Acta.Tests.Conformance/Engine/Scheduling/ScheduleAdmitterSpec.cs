using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
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
public sealed class ScheduleAdmitterSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Clock = new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
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
        Assert.Equal(EvaluationStatusCode.Scheduled, schedule.EvaluationStatus);
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
        Assert.Equal(EvaluationStatusCode.WaitingForCapacity, waiting.EvaluationStatus);
        Assert.Null(waiting.NextEvaluationAtUtc);
        Assert.Equal(first.AddMinutes(5), waiting.NextOccurrenceAtUtc);

        // The attempt settles the firing, which wakes the member; the next pass admits the waiting occurrence.
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        var woken = await ScheduleAsync(slotId, ct);
        Assert.Equal(EvaluationStatusCode.Scheduled, woken.EvaluationStatus);
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
