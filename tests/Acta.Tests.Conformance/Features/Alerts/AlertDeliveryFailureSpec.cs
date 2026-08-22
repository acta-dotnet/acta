using System.Diagnostics;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the <c>sys.alerts</c> deliver-phase failure branches: a throwing transport retries with
/// backoff (bumping <c>retry_count</c> and setting <c>retry_after_utc</c>), reaches terminal Failed at max
/// retries, a missing transport fails immediately (Permanent path, <c>retry_count</c> stays 0), and a
/// null-<c>job_id</c> (framework) alert is not dropped by the LEFT JOIN and delivers successfully. Also
/// the whole-pass facts about an incident that outlives its first notification: settling a send writes
/// the next reminder's instant, an automatic alert keeps re-notifying on that cadence while it is open, a
/// delivered manual one never does, a failed manual one is re-attempted until it lands, and a resolve
/// arriving mid-send leaves the settlement without effect.
/// </summary>
[ConformanceSpec(
    "alert.delivery-failure",
    "Alert delivery retries with backoff, goes terminal, and reminds open incidents",
    Area = "Alerts",
    Contract = "A throwing transport retries to terminal Failed, a missing transport fails at once, and settling schedules the reminder that re-sends an open automatic alert.",
    Arrange = "Pending alerts of both origins are seeded against a throwing transport, a missing transport kind, and a transport that resolves the row while it sends.",
    Act = "The delivery phase settles each attempt while the clock advances past each backoff and retry_after_utc is moved to stage a due reminder.",
    Assert = "Retries park until Failed, a due automatic reminder re-sends and reschedules, a delivered manual alert never reminds, and a mid-send resolve voids the settle."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetDeliverableAlertsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.UpdateAlertDeliveryAsync))]
public abstract class AlertDeliveryFailureSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const string ThrowingKind = "spy-throw";
    private const string MissingKind = "spy-missing";
    private const string ResolvingKind = "spy-resolve-mid-send";
    private const string PausingKind = "spy-pause-mid-send";

    // Long enough that the pause survives the coarsest instant the three providers store (whole
    // milliseconds), short enough to be beneath notice in a suite. Only its lower bound is relied on.
    private static readonly TimeSpan SendPause = TimeSpan.FromMilliseconds(25);

    // The reminder spacing these passes settle with; a fact that wants the reminder to come round moves
    // the row's instant itself rather than waiting.
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);

    // Half a unit of rounding either way at the coarsest instant the three providers store (whole
    // milliseconds on SQL Server's datetime2(3) and on SQLite's epoch-ms), rounded up to one whole unit.
    // This is the only slack in the reminder assertion; everything else about it is measured.
    private static readonly TimeSpan ProviderInstantPrecision = TimeSpan.FromMilliseconds(1);

    // Staging instants for "already come round" and "not for a long while", picked far enough from any
    // real or fake clock in play that neither can drift into the other.
    private static readonly DateTime LongPast = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarFuture = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private AdvancableClock Clock { get; set; } = null!;
    private TestAlertChannelRegistry Channels { get; } = new();
    private ResolvingTransport Resolver { get; } = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register FakeClock before UseActa so the TryAddSingleton<IActaClock, DbClock> no-ops.
        Clock = new AdvancableClock(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        // Add spy transports; LogAlertTransport (kind="log") is still registered by UseActa.
        services.AddSingleton<IAlertTransport>(new ThrowingTransport());
        services.AddSingleton<IAlertTransport>(new PausingTransport());
        services.AddSingleton<IAlertTransport>(Resolver);
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<IAlertChannelRegistry>(Channels);
    }

    [Fact(DisplayName = "Throwing transport bumps retry_count and parks with a backoff instant")]
    public async Task Throwing_transport_bumps_retry_count_and_parks_with_backoff()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-throw", ThrowingKind, ct);
        await RaiseAlertAsync(Db, "ch-throw", jobId: null, ct);

        // Pass 1: transport throws → Retryable → RetryAfter(50), retry_count=1.
        await RunDeliveryAsync(maxRetries: 5, ct);

        var row1 = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, row1.DeliveryStatusCode);
        Assert.Equal((byte)1, row1.RetryCount);
        Assert.NotNull(row1.RetryAfterUtc);
        Assert.True(row1.RetryAfterUtc > Clock.Now, "retry_after_utc must be in the future relative to when delivery ran");

        // Advance past the backoff window then run a second pass → retry_count=2.
        Clock.AdvanceTo(row1.RetryAfterUtc!.Value.AddSeconds(1));
        await RunDeliveryAsync(maxRetries: 5, ct);

        var row2 = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, row2.DeliveryStatusCode);
        Assert.Equal((byte)2, row2.RetryCount);
        Assert.NotNull(row2.RetryAfterUtc);
    }

    [Fact(DisplayName = "Transport throws at max retries and marks the alert terminal Failed")]
    public async Task Transport_throws_at_max_retries_marks_alert_terminal_failed()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-maxretry", ThrowingKind, ct);
        await RaiseAlertAsync(Db, "ch-maxretry", jobId: null, ct);

        // maxRetries=2: pass 1 → retry_count=1 (1 < 2), RetryAfter.
        await RunDeliveryAsync(maxRetries: 2, ct);

        var row1 = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, row1.DeliveryStatusCode);
        Assert.Equal((byte)1, row1.RetryCount);

        // Pass 2 → retry_count=2 (2 >= 2), terminal Failed.
        Clock.AdvanceTo(row1.RetryAfterUtc!.Value.AddSeconds(1));
        await RunDeliveryAsync(maxRetries: 2, ct);

        var row2 = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Failed, row2.DeliveryStatusCode);
        Assert.Equal((byte)2, row2.RetryCount);
        // Out of retries, but the incident is open, so the row carries a reminder instant rather than
        // nothing. Parked forward because this spec's clock lives in its own era, which would otherwise
        // make that instant already due and turn pass 3 into a reminder instead of the no-op it tests.
        Assert.NotNull(row2.RetryAfterUtc);
        await ScheduleAsync(row2.Id, FarFuture, ct);

        // Pass 3: Failed is terminal for the retry path: a further pass leaves it unchanged.
        await RunDeliveryAsync(maxRetries: 2, ct);
        var row3 = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Failed, row3.DeliveryStatusCode);
        Assert.Equal((byte)2, row3.RetryCount);
    }

    [Fact(DisplayName = "Missing transport marks the alert Failed immediately on the first pass")]
    public async Task Missing_transport_marks_alert_failed_immediately()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-missing", MissingKind, ct);
        await RaiseAlertAsync(Db, "ch-missing", jobId: null, ct);

        // Permanent failure: no retry, retry_count stays 0.
        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Failed, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        // A reminder is still scheduled: the transport may be registered by the time it comes round.
        Assert.NotNull(row.RetryAfterUtc);
    }

    [Fact(DisplayName = "Missing configured channel marks the alert Failed immediately")]
    public async Task Missing_configured_channel_marks_alert_failed_immediately()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaiseAlertAsync(Db, "not-configured", jobId: null, ct);

        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Failed, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        // As above: a reminder is scheduled, because the channel may be configured before it comes round.
        Assert.NotNull(row.RetryAfterUtc);
    }

    [Fact(DisplayName = "Disabled channel suppresses the alert and is not reread")]
    public async Task Disabled_channel_suppresses_alert()
    {
        var ct = TestContext.Current.CancellationToken;

        RegisterChannel("ch-disabled", "log", AlertChannelStatusCode.Disabled, AlertSeverityCode.Info);
        await RaiseAlertAsync(Db, "ch-disabled", jobId: null, ct);

        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
    }

    [Fact(DisplayName = "Deprecated channel suppresses the alert and is not reread")]
    public async Task Deprecated_channel_suppresses_alert()
    {
        var ct = TestContext.Current.CancellationToken;

        RegisterChannel("ch-deprecated", "log", AlertChannelStatusCode.Deprecated, AlertSeverityCode.Info);
        await RaiseAlertAsync(Db, "ch-deprecated", jobId: null, ct);

        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
    }

    [Fact(DisplayName = "Below min severity suppresses the alert and is not reread")]
    public async Task Below_min_severity_suppresses_alert()
    {
        var ct = TestContext.Current.CancellationToken;

        RegisterChannel("ch-floor", "log", AlertChannelStatusCode.Active, AlertSeverityCode.Error);
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Info,
            AlertKindCode.FinalFailure,
            title: "test-alert",
            message: "test-message",
            channelName: "ch-floor",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );

        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
    }

    [Fact(DisplayName = "Null-job-id alert is returned by GetDeliverableAlerts and delivers successfully")]
    public async Task Null_job_id_alert_is_returned_and_delivers()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use the built-in log transport (registered by UseActa) for a succeeding delivery.
        await SeedChannelAsync(Db, "ch-log", "log", ct);
        await RaiseAlertAsync(Db, "ch-log", jobId: null, ct);

        // Confirm the alert IS returned (not dropped by the LEFT JOIN on job → definitions).
        var due = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        Assert.Null(due.JobId);
        Assert.Null(due.RunbookUrl); // no job → no definition → runbook is null

        // Drive delivery: LogAlertTransport returns Delivered → Delivered(30), retry_count stays 0.
        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        Assert.Null(row.JobId);
    }

    [Fact(DisplayName = "Delivering an automatic alert schedules the next reminder, which re-sends and reschedules")]
    public async Task Delivering_an_automatic_alert_schedules_and_repeats_the_reminder()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-log", "log", ct);
        await RaiseAlertAsync(Db, "ch-log", jobId: null, ct);

        var firstPass = await RunDeliveryAsync(maxRetries: 5, ct);
        var delivered = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, delivered.DeliveryStatusCode);
        // The settlement wrote the next notification's instant, one interval out on the job's own clock.
        AssertScheduledOneIntervalOut(delivered.RetryAfterUtc, firstPass);

        // Before it comes round, a pass re-sends nothing. (Staging the instant writes only that column,
        // so an unchanged version is proof the pass itself did nothing.)
        await ScheduleAsync(delivered.Id, FarFuture, ct);
        await RunDeliveryAsync(maxRetries: 5, ct);
        Assert.Equal(delivered.Version, Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Version);

        // The instant arrives; the incident is still open, so it is notified again - and the reminder's
        // own settlement schedules the one after it, so the cadence continues without any other input.
        await ScheduleAsync(delivered.Id, LongPast, ct);
        var due = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        var reminderPass = await RunDeliveryAsync(maxRetries: 5, ct);

        var reminded = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, reminded.DeliveryStatusCode);
        Assert.Equal((byte)0, reminded.RetryCount);
        Assert.Equal(due.Version + 1, reminded.Version);
        // Bounded by the pass that wrote it, not the one before: each settlement measures from its own.
        AssertScheduledOneIntervalOut(reminded.RetryAfterUtc, reminderPass);
    }

    [Fact(DisplayName = "A reminder is stamped from the settlement, not from the instant the pass began")]
    public async Task Reminder_is_stamped_from_the_settlement_not_from_the_pass_start()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-pause", PausingKind, ct);
        await RaiseAlertAsync(Db, "ch-pause", jobId: null, ct);

        // A pass reads the clock once and then spends time: the generate drain carries a 30-second
        // budget of its own, and delivery adds a transport round trip per row - here, a transport that
        // takes a moment, which is the only thing this staging needs to be true.
        var passStart = Clock.Now;
        var pass = await RunDeliveryAsync(maxRetries: 5, ct);

        // So the reminder sits strictly past one interval from the instant the pass began, by whatever
        // the send cost. Measured from that instant instead it would land exactly on it, having eaten
        // the send's own time - and a short backoff so measured can be stamped already elapsed.
        var delivered = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, delivered.DeliveryStatusCode);
        Assert.NotNull(delivered.RetryAfterUtc);
        Assert.True(
            delivered.RetryAfterUtc > passStart + ReminderInterval,
            $"expected a reminder past {passStart + ReminderInterval:O}, found {delivered.RetryAfterUtc:O}"
        );

        // And past it by the send, not by an unbounded amount: the settlement's offset is time this pass
        // actually spent, so the measured duration caps it however slow the machine underneath is. Same
        // reasoning as AssertScheduledOneIntervalOut - the elapsed time is measured, never tolerated.
        var settledAtLatest = passStart + ReminderInterval + pass + ProviderInstantPrecision;
        Assert.True(
            delivered.RetryAfterUtc <= settledAtLatest,
            $"expected a reminder no later than {settledAtLatest:O} (a settlement inside a {pass.TotalMilliseconds:F1} ms pass), "
                + $"found {delivered.RetryAfterUtc:O}"
        );
    }

    [Fact(DisplayName = "A delivered manual alert is never reminded: its caller owns the incident")]
    public async Task Delivered_manual_alert_is_never_reminded()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-log", "log", ct);
        await RaiseAlertAsync(Db, "ch-log", jobId: null, ct, AlertOriginCode.Manual);

        await RunDeliveryAsync(maxRetries: 5, ct);

        // One notification per incident, and no schedule left behind. A ctx.AlertAsync is one handler's
        // statement at one moment; Acta cannot tell whether it still holds, so it does not nag about it.
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
        Assert.Null(row.RetryAfterUtc);
        Assert.Null(row.ResolvedAtUtc);

        // Nothing is due, on this pass or any other: an unscheduled row cannot come round.
        await RunDeliveryAsync(maxRetries: 5, ct);
        Assert.Equal(row.Version, Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Version);
    }

    [Fact(DisplayName = "A manual alert whose send failed is re-attempted on the reminder cadence until it lands")]
    public async Task Failed_manual_alert_is_reattempted_on_the_reminder_cadence()
    {
        var ct = TestContext.Current.CancellationToken;

        // No transport for this kind: the send fails permanently on the first pass.
        await SeedChannelAsync(Db, "ch-manual", MissingKind, ct);
        await RaiseAlertAsync(Db, "ch-manual", jobId: null, ct, AlertOriginCode.Manual);

        var failingPass = await RunDeliveryAsync(maxRetries: 5, ct);
        var failed = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Failed, failed.DeliveryStatusCode);
        // Origin does not exempt a failed send: nobody has been told yet, so it is scheduled to try again.
        AssertScheduledOneIntervalOut(failed.RetryAfterUtc, failingPass);

        // The instant comes round with the transport now available, and the alert finally lands.
        await ScheduleAsync(failed.Id, LongPast, ct);
        RegisterChannel("ch-manual", "log", AlertChannelStatusCode.Active, AlertSeverityCode.Info);
        await RunDeliveryAsync(maxRetries: 5, ct);

        var landed = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, landed.DeliveryStatusCode);
        // Delivered and manual: the re-attempts stop here rather than turning into a daily reminder.
        Assert.Null(landed.RetryAfterUtc);
    }

    [Fact(DisplayName = "A delivered send hands the next reminder a whole retry budget, not the one it spent")]
    public async Task Delivered_settlement_resets_the_retry_budget_for_the_next_series()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-swap", "log", ct);
        await RaiseAlertAsync(Db, "ch-swap", jobId: null, ct);
        var raised = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        // Mid-series: four attempts spent, one left under the cap this pass runs with.
        Assert.Equal(1, await Db.From<JobAlert>().Where(a => a.Id == raised.Id).UpdateOnlyAsync(() => new JobAlert { RetryCount = 4 }, ct));

        // The fifth attempt lands. retry_count is the budget for a send series, and that series is over.
        await RunDeliveryAsync(maxRetries: 5, ct);
        var delivered = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, delivered.DeliveryStatusCode);
        Assert.Equal((byte)0, delivered.RetryCount);

        // The reminder comes round, the incident is still open, and the channel's transport now throws.
        // Carrying the old count would have put this reminder at 5 of 5 on its first throw - terminal
        // without ever being retried. With a fresh budget it enters the curve like any first attempt.
        await ScheduleAsync(delivered.Id, LongPast, ct);
        RegisterChannel("ch-swap", ThrowingKind, AlertChannelStatusCode.Active, AlertSeverityCode.Info);
        await RunDeliveryAsync(maxRetries: 5, ct);

        var reminded = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, reminded.DeliveryStatusCode);
        Assert.Equal((byte)1, reminded.RetryCount);
        Assert.NotNull(reminded.RetryAfterUtc);
    }

    [Fact(DisplayName = "A resolve that lands while the transport is sending leaves the settlement without effect")]
    public async Task Resolve_during_the_send_voids_the_settlement()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-resolve", ResolvingKind, ct);
        await RaiseAlertAsync(Db, "ch-resolve", jobId: null, ct);
        var raised = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        // The transport resolves the row from inside SendAsync, then reports success: the exact window
        // where an operator's resolve races an attempt already handed to a transport. The write applies
        // what a resolve applies - resolved, its queued Pending settled to Suppressed, version bumped -
        // rather than going through the resolve verb, which would need a real job to audit against.
        Resolver.ResolveDuringSend(async () =>
            Assert.Equal(
                1,
                await Db.From<JobAlert>()
                    .Where(a => a.Id == raised.Id)
                    .UpdateOnlyAsync(
                        () =>
                            new JobAlert
                            {
                                ResolvedAtUtc = DateTime.UtcNow,
                                DeliveryStatusCode = AlertDeliveryStatusCode.Suppressed,
                                RetryAfterUtc = null,
                                Version = raised.Version + 1,
                            },
                        ct
                    )
            )
        );

        await RunDeliveryAsync(maxRetries: 5, ct);

        // The settle lost the compare-and-swap, so the resolved row keeps the state the resolve left it in.
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.NotNull(row.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        Assert.Equal(raised.Version + 1, row.Version);

        // And the resolved row is not picked up again on the next pass.
        await RunDeliveryAsync(maxRetries: 5, ct);
        Assert.Equal(raised.Version + 1, Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Version);
    }

    // --- Helpers ---

    private Task SeedChannelAsync(IDbSession db, string name, string transportKind, CancellationToken ct)
    {
        RegisterChannel(name, transportKind, AlertChannelStatusCode.Active, AlertSeverityCode.Info);
        return Task.CompletedTask;
    }

    private void RegisterChannel(string name, string transportKind, AlertChannelStatusCode status, AlertSeverityCode minSeverity) =>
        Channels.Register(TestNamespace, new AlertChannelDeclaration(name, transportKind, Endpoint: "endpoint", status, minSeverity));

    private Task RaiseAlertAsync(
        IDbSession db,
        string channel,
        long? jobId,
        CancellationToken ct,
        AlertOriginCode origin = AlertOriginCode.Automatic
    ) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            origin,
            AlertSeverityCode.Error,
            origin == AlertOriginCode.Manual ? AlertKindCode.Manual : AlertKindCode.FinalFailure,
            title: "test-alert",
            message: "test-message",
            channelName: channel,
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );

    /// <summary>
    /// Runs one pass and returns the wall time it took. The pass stamps its settlements from a monotonic
    /// offset off the clock it read on entry, so that elapsed time is not noise a fact has to tolerate -
    /// it is the exact quantity a reminder instant carries, and measuring it here is what lets
    /// <see cref="AssertScheduledOneIntervalOut"/> bound the answer instead of guessing at it. Measured
    /// around the whole call, so it can only over-cover the settlement's own offset, never under-cover it.
    /// </summary>
    private async Task<TimeSpan> RunDeliveryAsync(int maxRetries, CancellationToken ct)
    {
        var alerts = new AlertsJob(
            Services.GetRequiredService<IAlertStore>(),
            Clock,
            Channels,
            Services.GetRequiredService<IAlertTransportRegistry>(),
            Options.Create(new JobsOptions { AlertDeliveryMaxRetries = maxRetries, AlertReminderInterval = ReminderInterval })
        );

        var started = Stopwatch.GetTimestamp();
        await alerts.Handle(BuildAlertsCtx(), ct);
        return Stopwatch.GetElapsedTime(started);
    }

    // Moves the row's scheduled instant, which is how these facts travel in time. The delivery read
    // compares retry_after_utc against the DATABASE's clock while settlement writes it from the spec's
    // fake IActaClock, so the two live in different eras: what a settle scheduled is asserted against the
    // fake clock, and whether it has come round is staged here against the real one.
    private async Task ScheduleAsync(long alertId, DateTime whenUtc, CancellationToken ct) =>
        Assert.Equal(
            1,
            await Db.From<JobAlert>().Where(a => a.Id == alertId).UpdateOnlyAsync(() => new JobAlert { RetryAfterUtc = whenUtc }, ct)
        );

    /// <summary>
    /// The settlement scheduled the next notification one interval past the instant it settled - which is
    /// the instant the pass read the clock plus however long the pass then took, never the clock read
    /// alone (<c>AlertSettlementClock</c>). The window is therefore bounded by
    /// <paramref name="passDuration"/>, the wall time this spec measured around that very pass, rather
    /// than by a tolerance: an upper edge that is the real elapsed time is exact, while a fixed one is
    /// only a bet on how fast the machine is. A bet is what this used to be, and a loaded runner that
    /// spent 1.1 s inside one pass called a correct reminder wrong.
    /// </summary>
    private void AssertScheduledOneIntervalOut(DateTime? scheduledUtc, TimeSpan passDuration)
    {
        Assert.NotNull(scheduledUtc);
        var settledAtEarliest = Clock.Now + ReminderInterval - ProviderInstantPrecision;
        var settledAtLatest = Clock.Now + ReminderInterval + passDuration + ProviderInstantPrecision;
        Assert.True(
            scheduledUtc!.Value >= settledAtEarliest && scheduledUtc.Value <= settledAtLatest,
            $"expected a reminder in [{settledAtEarliest:O}, {settledAtLatest:O}] - one interval past a settlement inside a "
                + $"{passDuration.TotalMilliseconds:F1} ms pass - found {scheduledUtc:O}"
        );
    }

    private RuntimeJobContext BuildAlertsCtx()
    {
        var slot = new ClaimedJob(
            JobId: 1L,
            JobRef: Guid.Empty,
            NamespaceId: TestNamespaceId,
            DefinitionId: 0,
            TenantId: null,
            ExecutionNumber: 1,
            DeduplicationKey: null,
            CorrelationKey: null,
            ExclusiveKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FailureCount: 0,
            Version: 0
        );
        return new RuntimeJobContext(
            slot,
            jobName: "sys.alerts",
            namespaceName: TestNamespace,
            namespaceId: TestNamespaceId,
            leaseTtlSeconds: 180,
            jobStore: Services.GetRequiredService<IJobStore>(),
            signalStore: Services.GetRequiredService<ISignalStore>(),
            alerts: Services.GetRequiredService<IAlertSink>(),
            executionStore: Services.GetRequiredService<IExecutionStore>(),
            serializers: Services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            lockStore: Services.GetRequiredService<ILockStore>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }

    // --- Inner types ---

    private sealed class AdvancableClock(DateTime initialUtc) : IActaClock
    {
        private long _ticks = initialUtc.Ticks;

        public DateTime Now => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now);

        public void AdvanceTo(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
    }

    private sealed class ThrowingTransport : IAlertTransport
    {
        public string TransportKind => ThrowingKind;

        public Task<AlertDeliveryOutcome> SendAsync(AlertNotification n, AlertTarget t, CancellationToken ct) =>
            throw new InvalidOperationException("Test: simulated transport failure.");
    }

    /// <summary>
    /// A transport that takes a moment to send, the way every real one does. Nothing here waits on a
    /// clock to reach a value: the pause is the staging, and the fact it feeds asserts only that the
    /// settlement lands after it, which holds for any pause the platform actually takes.
    /// </summary>
    private sealed class PausingTransport : IAlertTransport
    {
        public string TransportKind => PausingKind;

        public async Task<AlertDeliveryOutcome> SendAsync(AlertNotification n, AlertTarget t, CancellationToken ct)
        {
            await Task.Delay(SendPause, ct);
            return AlertDeliveryOutcome.Delivered;
        }
    }

    /// <summary>
    /// A transport that runs one staged action inside the send, then reports success. This is how the
    /// resolve-versus-in-flight-attempt race is tested without racing anything: the window between the
    /// delivery read handing out a row and the settle writing it back is normally microseconds wide, so a
    /// second thread would only prove which side happened to win on that run. Suspending the send instead
    /// makes the interleaving the spec's choice and the outcome the same on every provider.
    /// </summary>
    private sealed class ResolvingTransport : IAlertTransport
    {
        private Func<Task>? _duringSend;

        public string TransportKind => ResolvingKind;

        public void ResolveDuringSend(Func<Task> action) => Interlocked.Exchange(ref _duringSend, action);

        public async Task<AlertDeliveryOutcome> SendAsync(AlertNotification n, AlertTarget t, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _duringSend, null) is { } action)
            {
                await action();
            }

            return AlertDeliveryOutcome.Delivered;
        }
    }

    private sealed class TestAlertChannelRegistry : IAlertChannelRegistry
    {
        private readonly Dictionary<(string Namespace, string Name), AlertChannelDeclaration> _channels = [];

        public void Register(string namespaceName, AlertChannelDeclaration channel) => _channels[(namespaceName, channel.Name)] = channel;

        public AlertChannelDeclaration? Resolve(string namespaceName, string channelName) =>
            _channels.TryGetValue((namespaceName, channelName), out var channel) ? channel : null;

        public bool IsConfigured(string namespaceName, string channelName) => Resolve(namespaceName, channelName) is not null;

        public IReadOnlyCollection<string> NamesForNamespace(string namespaceName) =>
            _channels
                .Keys.Where(k => string.Equals(k.Namespace, namespaceName, StringComparison.Ordinal))
                .Select(k => k.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
    }
}
