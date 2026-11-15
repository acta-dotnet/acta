using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Ledger;

/// <summary>
/// Conformance for <c>GetOverview</c>: the one-row health summary that counts Ready, Executing,
/// and Failed jobs, the oldest due Ready age, unresolved alert counts, worker health counts, and
/// due-soon schedule count, optionally scoped to a namespace.
/// </summary>
[ConformanceSpec(
    "get-overview.health-counters",
    "GetOverview returns accurate health counters scoped to a namespace and globally",
    Area = "Reads",
    Contract = "GetOverview returns non-negative health counters for the requested namespace and a globally-scoped result that is a superset of any namespace-scoped result."
)]
[Deploys("add-numbers", "job-wait-signal", "job-wait-signal-timeout")]
public sealed class GetOverviewSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetOverviewSpec>>
{
    [Fact(
        DisplayName = "Namespace-scoped counters are non-negative and reflect the enqueued job, and the global result is a superset of the namespace count"
    )]
    public async Task Namespace_scoped_ReadyCount_reflects_enqueued_job()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        await EnqueueTestOps.EnqueueJobsAsync(
            Services,
            [
                new JobEnqueueValues(
                    NamespaceName: TestNamespace,
                    JobName: "add-numbers",
                    Input: payload,
                    DeduplicationKey: Unique("overview")
                ),
            ],
            ct
        );

        var ns = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace), ct);

        Assert.True(ns.ReadyCount >= 1, $"Expected ReadyCount >= 1 in namespace {TestNamespace}, got {ns.ReadyCount}.");
        Assert.NotNull(ns.OldestReadyAgeSeconds);
        Assert.True(ns.OldestReadyAgeSeconds >= 0, $"OldestReadyAgeSeconds must be non-negative, got {ns.OldestReadyAgeSeconds}.");
        Assert.True(ns.ExecutingCount >= 0);
        Assert.True(ns.FailedCount >= 0);
        Assert.True(ns.UnresolvedAlertCount >= 0);
        Assert.True(ns.UnresolvedCriticalAlertCount >= 0);
        Assert.True(ns.DeadWorkerCount >= 0);
        Assert.True(ns.StaleWorkerCount >= 0);
        Assert.True(ns.DueSoonScheduleCount >= 0);
        Assert.True(ns.ExecutorCapacity >= 0);

        var global = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(), ct);

        Assert.True(
            global.ReadyCount >= ns.ReadyCount,
            $"Global ReadyCount ({global.ReadyCount}) must be >= namespace-scoped count ({ns.ReadyCount})."
        );
    }

    [Fact(DisplayName = "An unknown namespace returns all-zero counters and a null OldestReadyAgeSeconds")]
    public async Task Unknown_namespace_returns_zero_counters()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery("no-such-namespace-xyz"), ct);

        Assert.Equal(0, result.ReadyCount);
        Assert.Null(result.OldestReadyAgeSeconds);
        Assert.Equal(0, result.ExecutingCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.UnresolvedAlertCount);
        Assert.Equal(0, result.UnresolvedCriticalAlertCount);
        Assert.Equal(0, result.DeadWorkerCount);
        Assert.Equal(0, result.StaleWorkerCount);
        Assert.Equal(0, result.DueSoonScheduleCount);
        Assert.Equal(0, result.ExecutorCapacity);
        Assert.Null(result.ScheduleLagSeconds);
    }

    /// <summary>
    /// Drives deterministic, isolated state in this test's fresh namespace (RunSystemJobs = false,
    /// so no system jobs and no schedules) and pins all overview counters to exact values.
    /// <para>
    /// <list type="bullet">
    ///   <item>ReadyCount / ExecutingCount / FailedCount · driven via enqueue + claim + CompleteExecutions.</item>
    ///   <item>UnresolvedAlertCount / UnresolvedCriticalAlertCount · 2 / 1: one Error + one Critical raised via RaiseAlert.</item>
    ///   <item>DeadWorkerCount · 1: W1 aged 2 h, swept by MarkDeadWorkers with 60 s window.</item>
    ///   <item>StaleWorkerCount · lease flip: W2 fresh → 0, W2 aged a minute past the configured lease → 1.</item>
    ///   <item>DueSoonScheduleCount · 0: dueSoonSeconds=1 and the slot cursors are parked a day out, so the one-second window can never include them.</item>
    ///   <item>ExecutorCapacity · lease flip, inverted from StaleWorkerCount: W2 fresh → 1 (its single slot), W2 aged → 0.</item>
    ///   <item>ScheduleLagSeconds · null: the parked cursors are a day ahead, so no live schedule is past due.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact(DisplayName = "Backlog counts a Suspended wait only once it has actually expired")]
    public async Task Backlog_counts_a_suspended_wait_only_once_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);

        // An unbounded wait is never backlog: no worker will ever claim it, and only a raise can move it.
        var unbounded = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(unbounded, ct));

        // Nor is a bounded one while its deadline is still ahead: a queue of long approval waits is
        // work nobody is behind on, and counting it would make the gauge cry wolf.
        var bounded = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(bounded, ct));

        var waiting = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);
        Assert.Equal(before.ReadyCount, waiting.ReadyCount);

        // Past the deadline it is backlog, because the claim will now take it and a worker is what it
        // is waiting for.
        await Db.From<JobStateEntity>()
            .Where(r => r.Id == bounded.JobId)
            .UpdateOnlyAsync(() => new JobStateEntity { NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1) }, ct);

        var expired = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);
        Assert.Equal(before.ReadyCount + 1, expired.ReadyCount);
        Assert.NotNull(expired.OldestReadyAgeSeconds);
    }

    [Fact(DisplayName = "Driven state pins all overview counters to exact values in an isolated namespace")]
    public async Task Driven_state_pins_exact_overview_counters()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        const int LeaseTtl = 300; // 5 min; no heartbeat in this test

        // W1: single worker registered by Runtime.InitializeAsync in this unique namespace.
        var w1 = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(w1);

        // ── Baseline snapshot ─────────────────────────────────────────────────────────────────────
        // The base's deploy seeds one Ready slot job for every schedule whose environments list
        // is empty (currently: recurring-ping with "every-5-minutes"). Take a before-snapshot now so
        // ReadyCount assertions use deltas and remain correct if the manifest changes.
        var before = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);
        // Confirm the namespace is otherwise clean (fresh, unique per test-instance).
        Assert.Equal(0, before.ExecutingCount);
        Assert.Equal(0, before.FailedCount);
        Assert.Equal(0, before.UnresolvedAlertCount);
        Assert.Equal(0, before.DeadWorkerCount);
        Assert.Equal(0, before.StaleWorkerCount);

        // ── Job counters ───────────────────────────────────────────────────────────────────────────
        var j1 = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        var j2 = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);
        var j3 = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))), ct);
        var j4 = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(4, 5))), ct);

        // j3 → Executing: claim, leave running (no CompleteExecutions call).
        var c3 = Assert.Single(await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, w1!.Id, LeaseTtl, j3, ct));

        // j4 → Failed: claim + ctx.FailAsync-style complete (HandlerStatusCode = 200 bypasses the failure budget).
        var c4 = Assert.Single(await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, w1.Id, LeaseTtl, j4, ct));
        var cr4 = await Services
            .GetRequiredService<ExecutionOperations>()
            .CompleteAsync(
                w1.Id,
                CompletionTestOps.Landing(c4, succeeded: false) with
                {
                    HandlerStatusCode = (byte)JobStatusCode.Failed,
                },
                ct
            );
        Assert.Equal(CompleteExecutionsAction.Completed, cr4.Action);

        // ── Alerts ────────────────────────────────────────────────────────────────────────────────
        // Two unresolved alerts on j1: one Error (severity 3) and one Critical (severity 4).
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            j1.JobId,
            AlertSeverityCode.Error,
            AlertKindCode.Manual,
            "pin-error",
            "error body",
            "ops",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            j1.JobId,
            AlertSeverityCode.Critical,
            AlertKindCode.Manual,
            "pin-critical",
            "critical body",
            "ops",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        // ── Dead worker ───────────────────────────────────────────────────────────────────────────
        // Direct-stamp W1 Dead. Using the global MarkDeadWorkers reaper is racy on the shared parallel
        // schema (a concurrent global sweep races our own); stamping the exact status keeps
        // DeadWorkerCount deterministic. DeadWorkerCount is namespace-scoped.
        await Db.From<WorkerEntity>()
            .Where(w => w.Id == w1.Id)
            .UpdateOnlyAsync(() => new WorkerEntity { Status = WorkerStatusCode.Dead }, ct);

        // ── Stale-worker lease flip ───────────────────────────────────────────────────────────────
        // W2 is started fresh after W1 is dead and reads live, then aged a minute past the configured lease.
        var configuredLease = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var (_, w2Id) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            ownerTeam: "test",
            description: null,
            hostName: "host-pin-w2",
            deploymentVersion: "v2",
            engineVersion: null,
            dotnetVersion: null,
            processId: 0,
            maxConcurrentExecutors: 1,
            ct
        );
        await Db.From<WorkerEntity>()
            .Where(w => w.Id == w2Id)
            .UpdateOnlyAsync(() => new WorkerEntity { Status = WorkerStatusCode.Draining }, ct);

        // ── Fresh assertion: W2 heard from inside the lease ──────────────────────────────────────
        var ovFresh = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);

        // ReadyCount: delta from before (before includes slot jobs seeded by the deploy).
        Assert.Equal(before.ReadyCount + 2, ovFresh.ReadyCount); // j1, j2 added; j3 Executing, j4 Failed
        Assert.Equal(1, ovFresh.ExecutingCount); // j3 is Executing
        Assert.Equal(1, ovFresh.FailedCount); // j4 is Failed
        Assert.Equal(2, ovFresh.UnresolvedAlertCount); // 2 raised, none resolved
        Assert.Equal(1, ovFresh.UnresolvedCriticalAlertCount); // 1 Critical (severity = 40)
        Assert.Equal(1, ovFresh.DeadWorkerCount); // W1 swept Dead, scoped to TestNamespace
        Assert.Equal(0, ovFresh.StaleWorkerCount); // W2 Draining, heard from inside the lease
        Assert.Equal(1, ovFresh.ExecutorCapacity); // W2's single slot; W1 is Dead
        Assert.Equal(0, ovFresh.DueSoonScheduleCount); // dueSoonSeconds=1: harness parks slot cursors a day out
        Assert.NotNull(ovFresh.OldestReadyAgeSeconds);
        Assert.True(ovFresh.OldestReadyAgeSeconds >= 0);
        Assert.Null(ovFresh.ScheduleLagSeconds); // the harness parks slot cursors a day out, so none is past due

        // ── Aged assertion: W2 a minute past the lease ───────────────────────────────────────────
        // Well inside every dead-after window the suite's global reapers use (five minutes and up), so a concurrent
        // sweep never flips W2 Dead and inflates DeadWorkerCount (the off-by-one flake).
        var past = DateTime.UtcNow.AddSeconds(-(configuredLease + 60));
        await Db.From<WorkerEntity>().Where(w => w.Id == w2Id).UpdateOnlyAsync(() => new WorkerEntity { LastSeenAtUtc = past }, ct);
        var ovAged = await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace, DueSoonWindowSeconds: 1), ct);

        Assert.Equal(1, ovAged.StaleWorkerCount); // W2 Draining, silent past the lease
        // Capacity is StaleWorkerCount's exact inverse here: W1 is Dead and W2 is past the lease, so neither
        // contributes a slot. A capacity that ignored freshness would read 1.
        Assert.Equal(0, ovAged.ExecutorCapacity);
        Assert.Null(ovAged.ScheduleLagSeconds);
        // Cross-check that other counters are unchanged across the aging.
        Assert.Equal(before.ReadyCount + 2, ovAged.ReadyCount);
        Assert.Equal(1, ovAged.ExecutingCount);
        Assert.Equal(1, ovAged.FailedCount);
        Assert.Equal(1, ovAged.DeadWorkerCount);
        Assert.Equal(2, ovAged.UnresolvedAlertCount);
        Assert.Equal(1, ovAged.UnresolvedCriticalAlertCount);
    }
}
