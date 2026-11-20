using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s alert delete (<c>PurgeExpiredAlerts</c>): <c>alerts</c> rows past the one
/// retention window go whether or not delivery settled, an open incident included, and the delete answers how many of
/// them were undelivered, which the pass warns about once. Rows are aged a day inside or a day past the window, or the
/// cutoff is put in the future, so no real-time wait is needed.
/// </summary>
[ConformanceSpec(
    "purge-expired-alerts.sweeps",
    "Purge deletes aged alerts delivered or not and counts the undelivered",
    Area = "Retention",
    Contract = "Purge deletes alerts past the retention window whether or not delivery settled, and answers and warns how many of them were undelivered."
)]
[Deploys("add-numbers")]
public sealed partial class PurgeExpiredAlertsSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredAlertsSpec>>
{
    // A window the boundary facts age their rows a day inside or a day past.
    private const int WindowDays = 30;

    private readonly RetentionLog _log = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<ILogger<RetentionJob>>(_log);
    }

    [Fact(DisplayName = "Alerts past the window are purged settled or not, counted apart, and none inside the window is")]
    public async Task Aged_alerts_are_purged_whatever_their_delivery_status_under_two_separate_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Null deduplication keys always insert, so these land as three distinct rows: one settled
        // (Delivered) and two whose delivery never settled (Pending, Retrying).
        await RaiseAlertAsync(AlertDeliveryStatusCode.Delivered, ct);
        await RaiseAlertAsync(AlertDeliveryStatusCode.Pending, ct);
        await RaiseAlertAsync(AlertDeliveryStatusCode.Retrying, ct);

        // A day inside the window nothing goes, and neither count moves - which is also the negative behind
        // the maintenance pass's one warning: no undelivered row purged, nothing to warn about.
        await AgeAlertsAsync(ns, WindowDays - 1, ct);
        var keep = await RetentionTestOps.PurgeAsync(Services, ns, WindowDays, 1000, 50, ct);
        Assert.Equal(0, keep.Alerts);
        Assert.Equal(0, keep.UndeliveredAlertsPurged);
        Assert.Equal(3, (await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct)).Count);

        // A day past the same window every row goes: the settled row counts as settled, the two unsettled ones as
        // undelivered, and no row is counted as both. Drained, because a single sweep passes over a row another
        // transaction holds under READPAST.
        await AgeAlertsAsync(ns, WindowDays + 1, ct);
        var purged = await PurgeAlertsUntilGoneAsync(ns, ct, WindowDays);
        Assert.Equal(1, purged.Alerts);
        Assert.Equal(2, purged.UndeliveredAlertsPurged);
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
    }

    [Fact(DisplayName = "A pass that deletes undelivered alerts warns how many, and one that deletes none stays silent")]
    public async Task Undelivered_alerts_purged_are_warned_about_with_their_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        await RaiseAlertAsync(AlertDeliveryStatusCode.Delivered, ct);
        await RaiseAlertAsync(AlertDeliveryStatusCode.Pending, ct);
        await RaiseAlertAsync(AlertDeliveryStatusCode.Retrying, ct);

        await RetentionTestOps.PurgeAsync(Services, ns, WindowDays, 1000, 50, ct);
        Assert.Empty(_log.UndeliveredPurgedIn(TestNamespace));

        // A sweep may pass a row another transaction holds, so the warnings of the drain add up to the two rows.
        await AgeAlertsAsync(ns, WindowDays + 1, ct);
        var purged = await PurgeAlertsUntilGoneAsync(ns, ct, WindowDays);
        Assert.Equal(2, purged.UndeliveredAlertsPurged);
        Assert.Equal(2, _log.UndeliveredPurgedIn(TestNamespace).Sum());
    }

    private Task AgeAlertsAsync(int ns, int days, CancellationToken ct)
    {
        var agedAt = DateTime.UtcNow.AddDays(-days);
        return Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).UpdateOnlyAsync(() => new AlertEntity { CreatedAtUtc = agedAt }, ct);
    }

    [Fact(DisplayName = "A resolve-suppressed alert past the window is counted once, as settled")]
    public async Task Resolved_but_suppressed_alert_is_counted_as_settled_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Suppressed is what resolution leaves behind on a row that was never sent. It is a settled
        // delivery, so it must not be counted as undelivered too.
        await RaiseAlertAsync(AlertDeliveryStatusCode.Suppressed, ct);

        var purged = await PurgeAlertsUntilGoneAsync(ns, ct);

        Assert.Equal(1, purged.Alerts);
        Assert.Equal(0, purged.UndeliveredAlertsPurged);
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
    }

    [Fact(DisplayName = "An open incident past the window is purged and the next failure opens a fresh one")]
    public async Task Aged_open_incident_is_purged_and_its_identity_is_free_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var key = Unique("retention-incident");

        // One deduplication key raised twice: the second raise collapses onto the open row rather than
        // opening a second, because the partial unique index holds the identity while it is unresolved.
        await RaiseIncidentAsync(key, ct);
        await RaiseIncidentAsync(key, ct);
        var open = Assert.Single(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
        Assert.Null(open.ResolvedAtUtc);
        Assert.Equal(2, open.OccurrenceCount);

        // The incident is open AND its delivery never settled, so it goes counted as undelivered and as
        // nothing else: unresolved is no shield, and neither is an unfinished delivery.
        var purged = await PurgeAlertsUntilGoneAsync(ns, ct);
        Assert.Equal(1, purged.UndeliveredAlertsPurged);
        Assert.Equal(0, purged.Alerts);
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));

        // The delete freed the identity with the row, so the next failure on the same key opens a fresh
        // incident - a new ref, counting from one - rather than finding the key taken.
        await RaiseIncidentAsync(key, ct);
        var reopened = Assert.Single(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
        Assert.NotEqual(open.AlertRef, reopened.AlertRef);
        Assert.Equal(1, reopened.OccurrenceCount);
        Assert.Null(reopened.ResolvedAtUtc);
    }

    [Fact(DisplayName = "An open incident whose delivery settled is purged as settled and frees its key")]
    public async Task Aged_open_but_delivered_incident_is_purged_as_settled()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var key = Unique("retention-delivered-incident");

        // The sibling of the Pending-arm fact, and the combination neither covered: unresolved, so the
        // partial unique index still holds the key, but delivered, so it is not counted as undelivered.
        await RaiseIncidentAsync(key, ct, AlertDeliveryStatusCode.Delivered);
        await RaiseIncidentAsync(key, ct, AlertDeliveryStatusCode.Delivered);
        var open = Assert.Single(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
        Assert.Null(open.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, open.DeliveryStatusCode);
        Assert.Equal(2, open.OccurrenceCount);

        // Counted as settled and not as undelivered, so an operator reading the two counters is not told a
        // settled delivery went out unsent.
        var purged = await PurgeAlertsUntilGoneAsync(ns, ct);
        Assert.Equal(1, purged.Alerts);
        Assert.Equal(0, purged.UndeliveredAlertsPurged);
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));

        // Aging out an unresolved row has to release the filtered unique identity however it was counted, or
        // the next failure on that key would find the identity taken by a row that no longer exists.
        await RaiseIncidentAsync(key, ct, AlertDeliveryStatusCode.Delivered);
        var reopened = Assert.Single(await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct));
        Assert.NotEqual(open.AlertRef, reopened.AlertRef);
        Assert.Equal(1, reopened.OccurrenceCount);
        Assert.Null(reopened.ResolvedAtUtc);
    }

    /// <summary>
    /// Sweeps with the cutoff in the future, or <paramref name="retentionDays"/> back, until the namespace holds no
    /// alert row, and returns the counts across the whole drain. Every alert fact here purges rows it has just written,
    /// and a single sweep is allowed to pass one of them over - it stages its batch under READPAST /
    /// SKIP LOCKED, and a spec's own row can sit under a parallel spec's lock. How the row was counted, which is what
    /// these facts are about, is settled either way.
    /// </summary>
    private Task<RetentionSweepCounts> PurgeAlertsUntilGoneAsync(int ns, CancellationToken ct, int retentionDays = -1) =>
        RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            retentionDays,
            1000,
            50,
            async () => (await Db.From<AlertEntity>().Where(a => a.NamespaceId == ns).ToListAsync(ct)).Count == 0,
            ct
        );

    private Task RaiseIncidentAsync(
        string deduplicationKey,
        CancellationToken ct,
        AlertDeliveryStatusCode delivery = AlertDeliveryStatusCode.Pending
    ) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: "default",
            delivery,
            deduplicationKey,
            ct
        );

    private Task RaiseAlertAsync(AlertDeliveryStatusCode delivery, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: "default",
            delivery,
            deduplicationKey: null,
            ct
        );

    /// <summary>The warnings the sweeps write, read by the fact whose contract is the warning.</summary>
    private sealed partial class RetentionLog : ILogger<RetentionJob>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _lines = new();

        // The undelivered count each warning about the namespace names.
        public IEnumerable<int> UndeliveredPurgedIn(string jobNamespace) =>
            _lines
                .Where(entry =>
                    entry.Level == LogLevel.Warning
                    && entry.Line.Contains("alert-retention-cap", StringComparison.Ordinal)
                    && entry.Line.Contains($"({jobNamespace})", StringComparison.Ordinal)
                )
                .Select(entry => int.Parse(PurgedCount().Match(entry.Line).Groups[1].Value, CultureInfo.InvariantCulture));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _lines.Enqueue((logLevel, formatter(state, exception)));

        [GeneratedRegex(@"purged \((\d+) alerts\)")]
        private static partial Regex PurgedCount();
    }
}
