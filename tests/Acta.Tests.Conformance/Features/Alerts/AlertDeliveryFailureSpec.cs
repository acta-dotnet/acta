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
/// the two whole-pass facts about an incident that outlives its first notification: a delivered row still
/// unresolved past the reminder interval is re-sent and stays Delivered, and a resolve that lands while
/// the transport is mid-send leaves the settlement without effect.
/// </summary>
[ConformanceSpec(
    "alert.delivery-failure",
    "Alert delivery retries with backoff, goes terminal, and reminds open incidents",
    Area = "Alerts",
    Contract = "A throwing transport retries with backoff to terminal Failed, a missing transport fails at once, and an unresolved Delivered row is re-sent once per interval.",
    Arrange = "Pending alerts are seeded against a throwing transport, a missing transport kind, and a transport that resolves the row while it sends.",
    Act = "The delivery phase settles each attempt while the clock advances past each backoff and modified_at_utc is aged past the reminder interval.",
    Assert = "Retries park with backoff until Failed, a missing transport fails at once, an aged unresolved Delivered row is re-sent, and a mid-send resolve voids the settle."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetDeliverableAlertsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.UpdateAlertDeliveryAsync))]
public abstract class AlertDeliveryFailureSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const string ThrowingKind = "spy-throw";
    private const string MissingKind = "spy-missing";
    private const string ResolvingKind = "spy-resolve-mid-send";

    // The reminder spacing these facts read with: long enough that nothing qualifies by accident, so the
    // one fact that wants a reminder has to age the row past it on purpose.
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);

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
        Assert.Null(row2.RetryAfterUtc);

        // Pass 3: Failed is terminal: a further pass leaves it unchanged.
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
        Assert.Null(row.RetryAfterUtc);
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
        Assert.Null(row.RetryAfterUtc);
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
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ReminderInterval, ct));
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
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ReminderInterval, ct));
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
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ReminderInterval, ct));
    }

    [Fact(DisplayName = "Null-job-id alert is returned by GetDeliverableAlerts and delivers successfully")]
    public async Task Null_job_id_alert_is_returned_and_delivers()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use the built-in log transport (registered by UseActa) for a succeeding delivery.
        await SeedChannelAsync(Db, "ch-log", "log", ct);
        await RaiseAlertAsync(Db, "ch-log", jobId: null, ct);

        // Confirm the alert IS returned (not dropped by the LEFT JOIN on job → definitions).
        var due = Assert.Single(
            await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ReminderInterval, ct)
        );
        Assert.Null(due.JobId);
        Assert.Null(due.RunbookUrl); // no job → no definition → runbook is null

        // Drive delivery: LogAlertTransport returns Delivered → Delivered(30), retry_count stays 0.
        await RunDeliveryAsync(maxRetries: 5, ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
        Assert.Equal((byte)0, row.RetryCount);
        Assert.Null(row.JobId);
    }

    [Fact(DisplayName = "An unresolved Delivered alert is re-sent once it ages past the reminder interval and stays Delivered")]
    public async Task Aged_unresolved_delivered_alert_is_reminded_and_stays_delivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedChannelAsync(Db, "ch-log", "log", ct);
        await RaiseAlertAsync(Db, "ch-log", jobId: null, ct);

        await RunDeliveryAsync(maxRetries: 5, ct);
        var delivered = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, delivered.DeliveryStatusCode);

        // A pass while the row is young re-sends nothing.
        await RunDeliveryAsync(maxRetries: 5, ct);
        Assert.Equal(delivered.Version, Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Version);

        // Aged past the interval, the still-open incident is notified again. Settling the reminder
        // re-stamps modified_at_utc, so the next reminder waits a full interval from this send.
        await AgeModifiedAsync(delivered.Id, ReminderInterval + TimeSpan.FromHours(1), ct);
        var aged = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        await RunDeliveryAsync(maxRetries: 5, ct);

        var reminded = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, reminded.DeliveryStatusCode);
        Assert.Equal((byte)0, reminded.RetryCount);
        Assert.Equal(delivered.Version + 1, reminded.Version);
        // Measured against the aged stamp, not the first settle's: the two settles can land inside one
        // tick of the column's precision, and it is the reset from "an interval ago" that matters.
        Assert.True(reminded.ModifiedAtUtc > aged.ModifiedAtUtc, "settling the reminder must re-stamp modified_at_utc");

        // And with the stamp refreshed, the very next pass finds nothing due again.
        await RunDeliveryAsync(maxRetries: 5, ct);
        Assert.Equal(reminded.Version, Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Version);
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

    private Task RaiseAlertAsync(IDbSession db, string channel, long? jobId, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "test-alert",
            message: "test-message",
            channelName: channel,
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );

    private Task RunDeliveryAsync(int maxRetries, CancellationToken ct)
    {
        var alerts = new AlertsJob(
            Services.GetRequiredService<IAlertStore>(),
            Clock,
            Channels,
            Services.GetRequiredService<IAlertTransportRegistry>(),
            Options.Create(new JobsOptions { AlertDeliveryMaxRetries = maxRetries, AlertReminderInterval = ReminderInterval })
        );
        return alerts.Handle(BuildAlertsCtx(), ct);
    }

    // Pushes the row's modified_at_utc back so the reminder arm sees it as that old. The delivery read
    // measures that column against the database's own clock, so aging the column is what stages a
    // reminder - the spec's fake IActaClock cannot, and does not, move the server's now().
    private async Task AgeModifiedAsync(long alertId, TimeSpan age, CancellationToken ct) =>
        Assert.Equal(
            1,
            await Db.From<JobAlert>()
                .Where(a => a.Id == alertId)
                .UpdateOnlyAsync(() => new JobAlert { ModifiedAtUtc = DateTime.UtcNow - age }, ct)
        );

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
