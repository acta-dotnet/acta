using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the delivery read + settle ops (<c>GetDeliverableAlerts</c> / <c>UpdateAlertDelivery</c>)
/// against the incident model. Both arms of the read key off <c>retry_after_utc</c>: Pending and
/// RetryAfter are due when their instant elapses or is unset, and an unresolved Delivered or Failed row
/// is due when the reminder its settlement scheduled arrives, while Suppressed never is. A repeat
/// landing on the open incident does not move that schedule, which is the point of putting it there. A
/// resolved row is selected on neither arm and its queued delivery is settled by the resolve, and
/// settlement is a compare-and-swap on the version the read handed out, so an attempt that raced a
/// resolve writes nothing.
/// </summary>
[ConformanceSpec(
    "alert-delivery.read-and-settle",
    "Deliverable alerts read due rows, remind open incidents, and settle by version",
    Area = "Alerts",
    Contract = "Delivery selects unresolved rows whose retry_after_utc has come, on the retry arm or the reminder arm, and settles only at the version the read handed out.",
    Arrange = "Pending alerts are raised in the test namespace, some against a seeded job the resolve verb can close, with retry_after_utc moved to stage due-ness.",
    Act = "GetDeliverableAlerts reads due rows and UpdateAlertDelivery settles them at the version it handed out, interleaved with ResolveJobAlerts and repeat raises.",
    Assert = "A resolved alert is never selected and its Pending row is Suppressed, a due Delivered or Failed row is re-selected after a repeat, and a stale settle no-ops."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetDeliverableAlertsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.UpdateAlertDeliveryAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertDeliverySpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime PastInstant = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureInstant = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A real definition + job, so the automatic resolve (which filters on job_id, Automatic origin, and
    // the failure kinds) has something to close and ck_alerts_job_ref_pair is satisfied.
    private long JobIdValue;
    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "alert-delivery-def", ct: ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(DisplayName = "Pending alert is deliverable by channel name and settles Delivered")]
    public async Task Pending_alert_is_deliverable_then_settles_delivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync("ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await DueAsync(ct));
        Assert.Equal("ops", due.ChannelName);
        Assert.Equal(AlertSeverityCode.Error, due.Severity);

        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Delivered, due.RetryCount, null, ct));

        Assert.Empty(await DueAsync(ct));
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
    }

    [Fact(DisplayName = "RetryAfter redelivers only when due")]
    public async Task Retryable_settle_parks_in_retry_after_and_redelivers_when_due()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync("ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await DueAsync(ct));

        // Park with a future retry instant - not yet due, so the next read skips it.
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.RetryAfter, 1, FutureInstant, ct));
        Assert.Empty(await DueAsync(ct));

        // Re-park with an elapsed instant - now due again, carrying the bumped retry count. The settle
        // rides the version the previous settle left behind, which is what the next selection would hand out.
        Assert.True(await SettleAsync(due.AlertId, due.Version + 1, AlertDeliveryStatusCode.RetryAfter, 1, PastInstant, ct));
        var again = Assert.Single(await DueAsync(ct));
        Assert.Equal((byte)1, again.RetryCount);
        Assert.Equal(due.Version + 2, again.Version);
    }

    [Fact(DisplayName = "A freshly settled Failed row is not re-selected by the retry path")]
    public async Task Freshly_failed_row_is_not_reselected_by_the_retry_path()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync("ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Failed, 5, FutureInstant, ct));

        // Terminal for the retry path: arm 1 does not take a Failed row at all, and the reminder arm
        // only takes it once the instant this settle scheduled has come.
        Assert.Empty(await DueAsync(ct));
    }

    [Fact(DisplayName = "Suppressed is never redelivered, even carrying a due instant")]
    public async Task Suppressed_is_never_redelivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync("ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Suppressed, 0, null, ct));

        Assert.Empty(await DueAsync(ct));

        // Suppression was a routing decision about the channel, not a send that failed: re-sending would
        // only re-take it. So even a row deliberately stamped with an elapsed instant stays unselected -
        // the reminder arm is gated on the status, not only on the clock.
        await ScheduleAsync(due.AlertId, PastInstant, ct);
        Assert.Empty(await DueAsync(ct));
    }

    [Fact(DisplayName = "An alert resolved before the deliver pass is not selected and its Pending row is Suppressed")]
    public async Task Resolved_before_selection_is_not_delivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("resolve-before-select", ct);
        Assert.Single(await DueAsync(ct));

        Assert.Equal(1, await ResolveAsync(ct));

        Assert.Empty(await DueAsync(ct));
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.NotNull(row.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Null(row.RetryAfterUtc);
    }

    [Fact(DisplayName = "Resolve settles each delivery status per the settlement table and clears retry_after_utc")]
    public async Task Resolve_settles_delivery_per_the_settlement_table()
    {
        var ct = TestContext.Current.CancellationToken;

        // One incident per starting status, each on its own job so one resolve closes exactly one row.
        var pending = await StageAsync(null, retryAfter: null, ct);
        var retryAfter = await StageAsync(AlertDeliveryStatusCode.RetryAfter, FutureInstant, ct);
        var delivered = await StageAsync(AlertDeliveryStatusCode.Delivered, null, ct);
        var failed = await StageAsync(AlertDeliveryStatusCode.Failed, null, ct);
        var suppressed = await StageAsync(AlertDeliveryStatusCode.Suppressed, null, ct);

        foreach (var jobId in new[] { pending, retryAfter, delivered, failed, suppressed })
        {
            Assert.Equal(1, await ResolveAsync(jobId, ct));
        }

        var rows = (await ReadAlertsAsync(TestNamespaceId, ct)).ToDictionary(a => a.JobId!.Value);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, rows[pending].DeliveryStatusCode);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, rows[retryAfter].DeliveryStatusCode);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, rows[delivered].DeliveryStatusCode);
        Assert.Equal(AlertDeliveryStatusCode.Failed, rows[failed].DeliveryStatusCode);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, rows[suppressed].DeliveryStatusCode);
        Assert.All(rows.Values, r => Assert.NotNull(r.ResolvedAtUtc));
        Assert.All(rows.Values, r => Assert.Null(r.RetryAfterUtc));
        Assert.Empty(await DueAsync(ct));
    }

    [Fact(DisplayName = "A settle at the version a resolve has already superseded applies nothing")]
    public async Task Stale_settle_after_a_resolve_loses_the_compare_and_swap()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("resolve-vs-send", ct);

        // The shape of an attempt in flight: selected, then the row moves under it.
        var inFlight = Assert.Single(await DueAsync(ct));
        Assert.Equal(1, await ResolveAsync(ct));

        // The transport came back after the resolve landed. The CAS misses, so nothing is written.
        Assert.False(await SettleAsync(inFlight.AlertId, inFlight.Version, AlertDeliveryStatusCode.Delivered, 1, null, ct));

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.NotNull(row.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        Assert.Null(row.RetryAfterUtc);
    }

    [Fact(DisplayName = "An unresolved Delivered row is reminded once its scheduled instant arrives")]
    public async Task Unresolved_delivered_row_is_reminded_when_its_instant_arrives()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("delivered-reminder", ct);
        var due = Assert.Single(await DueAsync(ct));

        // Settling schedules the next notification; until that instant, the incident is open but has
        // just been notified about, so nothing is due.
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Delivered, due.RetryCount, FutureInstant, ct));
        Assert.Empty(await DueAsync(ct));

        // The instant arrives and the incident is still open, so it is re-notified.
        await ScheduleAsync(due.AlertId, PastInstant, ct);
        var reminder = Assert.Single(await DueAsync(ct));
        Assert.Equal(due.AlertId, reminder.AlertId);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, (await ReadAlertsAsync(TestNamespaceId, ct))[0].DeliveryStatusCode);
    }

    [Fact(DisplayName = "A repeat landing after the delivery settled does not postpone the reminder")]
    public async Task A_repeat_after_settlement_does_not_postpone_the_reminder()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("repeat-vs-reminder", ct);
        var due = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Delivered, due.RetryCount, PastInstant, ct));

        // The condition fires again while the incident is open. The raise collapses onto the same row,
        // bumping occurrence_count, modified_at_utc, and version - and leaving delivery alone.
        await RaiseAutomaticAsync("repeat-vs-reminder", ct);
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(2, row.OccurrenceCount);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);

        // The reminder still fires. This is the whole reason the schedule lives on retry_after_utc: a job
        // failing faster than the reminder interval re-stamps modified_at_utc on every repeat, so an
        // age-of-last-change rule would leave exactly the loudest outage permanently too young to remind.
        var reminder = Assert.Single(await DueAsync(ct));
        Assert.Equal(due.AlertId, reminder.AlertId);
        Assert.Equal(2, reminder.OccurrenceCount);
    }

    [Fact(DisplayName = "An unresolved Failed row is reminded rather than silenced forever")]
    public async Task Unresolved_failed_row_is_reminded_when_its_instant_arrives()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("failed-reminder", ct);
        var due = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Failed, 5, PastInstant, ct));

        var reminder = Assert.Single(await DueAsync(ct));
        Assert.Equal(due.AlertId, reminder.AlertId);
        Assert.Equal((byte)5, reminder.RetryCount);
    }

    [Fact(DisplayName = "A resolved Delivered row is never reminded: the resolve took its schedule away")]
    public async Task Resolved_delivered_row_is_never_reminded()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("resolved-no-reminder", ct);
        var due = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(due.AlertId, due.Version, AlertDeliveryStatusCode.Delivered, due.RetryCount, PastInstant, ct));

        // Due for a reminder at the moment it resolves - the tightest version of the race.
        Assert.Single(await DueAsync(ct));
        Assert.Equal(1, await ResolveAsync(ct));

        Assert.Empty(await DueAsync(ct));
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
        Assert.Null(row.RetryAfterUtc);
    }

    [Fact(DisplayName = "The fresh incident opened after a resolution delivers on its own")]
    public async Task Fresh_incident_after_a_resolution_delivers_independently()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAutomaticAsync("fresh-incident", ct);
        var first = Assert.Single(await DueAsync(ct));
        Assert.True(await SettleAsync(first.AlertId, first.Version, AlertDeliveryStatusCode.Failed, 5, null, ct));
        Assert.Equal(1, await ResolveAsync(ct));
        Assert.Empty(await DueAsync(ct));

        // The condition fires again on the same identity. Resolution is terminal, so this opens a second
        // row - and that row's delivery starts from scratch rather than inheriting the closed row's Failed.
        await RaiseAutomaticAsync("fresh-incident", ct);

        var fresh = Assert.Single(await DueAsync(ct));
        Assert.NotEqual(first.AlertId, fresh.AlertId);
        Assert.Equal((byte)0, fresh.RetryCount);
        Assert.True(await SettleAsync(fresh.AlertId, fresh.Version, AlertDeliveryStatusCode.Delivered, fresh.RetryCount, null, ct));

        var rows = (await ReadAlertsAsync(TestNamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(AlertDeliveryStatusCode.Failed, rows[0].DeliveryStatusCode);
        Assert.NotNull(rows[0].ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, rows[1].DeliveryStatusCode);
        Assert.Null(rows[1].ResolvedAtUtc);
    }

    // --- Helpers ---

    private Task<IReadOnlyList<DeliverableAlert>> DueAsync(CancellationToken ct) =>
        Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct);

    private Task<bool> SettleAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        byte retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(alertId, expectedVersion, status, retryCount, retryAfterUtc, ct);

    private Task<int> ResolveAsync(CancellationToken ct) => ResolveAsync(JobIdValue, ct);

    private async Task<int> ResolveAsync(long jobId, CancellationToken ct) =>
        await Services
            .GetRequiredService<IAlertStore>()
            .ResolveJobAlertsAsync(TestNamespaceId, jobId, await NextEventIdAsync(jobId, ct), ct);

    // Moves the row's scheduled instant without touching anything else, which is how these facts travel
    // in time: the delivery read compares retry_after_utc against the database's own clock, so rewinding
    // the column is the honest way to say "the moment arrived" and parking it forward says "not yet".
    private async Task ScheduleAsync(long alertId, DateTime whenUtc, CancellationToken ct) =>
        Assert.Equal(
            1,
            await Db.From<JobAlert>().Where(a => a.Id == alertId).UpdateOnlyAsync(() => new JobAlert { RetryAfterUtc = whenUtc }, ct)
        );

    // One incident on its own job, left in the requested delivery state; returns the job id the resolve
    // verb needs. A null status leaves the row as raised (Pending).
    private async Task<long> StageAsync(AlertDeliveryStatusCode? status, DateTime? retryAfter, CancellationToken ct)
    {
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
        await RaiseAutomaticAsync($"settlement-{status?.ToString() ?? "pending"}", jobId, ct);

        var row = Assert.Single(await Db.From<JobAlert>().Where(a => a.JobId == jobId).ToListAsync(ct));
        if (status is { } target)
        {
            Assert.True(await SettleAsync(row.Id, row.Version, target, row.RetryCount, retryAfter, ct));
        }

        return jobId;
    }

    private Task RaisePendingAsync(string channel, AlertSeverityCode severity, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertOriginCode.Automatic,
            severity,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: channel,
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );

    private Task RaiseAutomaticAsync(string deduplicationKey, CancellationToken ct) =>
        RaiseAutomaticAsync(deduplicationKey, JobIdValue, ct);

    // The shape the projector writes: Automatic origin, a failure kind, and a job the resolve verb can
    // scope to - the only rows ResolveJobAlerts closes.
    private Task RaiseAutomaticAsync(string deduplicationKey, long jobId, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            ct
        );
}
