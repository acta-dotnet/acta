using Acta.Features.Alerts;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Overview;
using Acta.Features.Workers;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Overview;

/// <summary>
/// Conformance for <c>GetOverview</c>: the one-row health summary that counts Ready, Executing,
/// and Failed jobs, the oldest due Ready age, unresolved alert counts, worker health counts, and
/// due-soon schedule count, optionally scoped to a namespace.
/// </summary>
[ConformanceSpec(
    "get-overview.health-counters",
    "GetOverview returns accurate health counters scoped to a namespace and globally",
    Area = "Reads",
    Contract = "GetOverview returns non-negative health counters for the requested namespace and a globally-scoped result that is a superset of any namespace-scoped result.",
    Arrange = "One job is enqueued in the test namespace.",
    Act = "The overview is read scoped to the namespace, globally, and for an unknown namespace.",
    Assert = "Namespace counters are non-negative and reflect the enqueued job, and the global result is a superset of any namespace-scoped result."
)]
[CoversStoreMethod(typeof(IOverviewStore), nameof(IOverviewStore.GetOverviewAsync))]
public abstract class GetOverviewSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private IOverviewStore Overview => Services.GetRequiredService<IOverviewStore>();

    [Fact(
        DisplayName = "Namespace-scoped counters are non-negative and reflect the enqueued job, and the global result is a superset of the namespace count"
    )]
    public async Task Namespace_scoped_ReadyCount_reflects_enqueued_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [
                new JobEnqueueRow(
                    NamespaceName: TestNamespace,
                    JobName: "add-numbers",
                    Input: payload,
                    DeduplicationKey: TestKey("overview")
                ),
            ],
            ct
        );

        var ns = await Overview.GetOverviewAsync(new OverviewQuery(TestNamespace, 180, 3600), ct);

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
        Assert.True(ns.JobCount >= 1, $"Expected JobCount >= 1 in namespace {TestNamespace}, got {ns.JobCount}.");
        Assert.True(ns.SystemJobCount >= 0);
        Assert.True(ns.JobCount >= ns.SystemJobCount, "JobCount includes system jobs, so it must be >= SystemJobCount.");

        var global = await Overview.GetOverviewAsync(new OverviewQuery(null, 180, 3600), ct);

        Assert.True(
            global.ReadyCount >= ns.ReadyCount,
            $"Global ReadyCount ({global.ReadyCount}) must be >= namespace-scoped count ({ns.ReadyCount})."
        );
        Assert.True(
            global.JobCount >= ns.JobCount,
            $"Global JobCount ({global.JobCount}) must be >= namespace-scoped count ({ns.JobCount})."
        );
    }

    [Fact(DisplayName = "An unknown namespace returns all-zero counters and a null OldestReadyAgeSeconds")]
    public async Task Unknown_namespace_returns_zero_counters()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Overview.GetOverviewAsync(new OverviewQuery("no-such-namespace-xyz", 180, 3600), ct);

        Assert.Equal(0, result.ReadyCount);
        Assert.Null(result.OldestReadyAgeSeconds);
        Assert.Equal(0, result.ExecutingCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.UnresolvedAlertCount);
        Assert.Equal(0, result.UnresolvedCriticalAlertCount);
        Assert.Equal(0, result.DeadWorkerCount);
        Assert.Equal(0, result.StaleWorkerCount);
        Assert.Equal(0, result.DueSoonScheduleCount);
        Assert.Equal(0, result.JobCount);
        Assert.Equal(0, result.SystemJobCount);
    }

    /// <summary>
    /// Drives deterministic, isolated state in this test's fresh namespace (RegisterFrameworkJobs = false,
    /// so no system jobs and no schedules) and pins all overview counters to exact values.
    /// <para>
    /// <list type="bullet">
    ///   <item>ReadyCount / ExecutingCount / FailedCount / JobCount — driven via enqueue + claim + StartExecution + CompleteExecution.</item>
    ///   <item>SystemJobCount — 0: no system job definitions registered.</item>
    ///   <item>UnresolvedAlertCount / UnresolvedCriticalAlertCount — 2 / 1: one Error + one Critical raised via RaiseJobAlert.</item>
    ///   <item>DeadWorkerCount — 1: W1 aged 2 h, swept by MarkDeadWorkers with 60 s window.</item>
    ///   <item>StaleWorkerCount — threshold flip: W2 aged 3 h; staleAfterSeconds=3600 → 1, staleAfterSeconds=14400 → 0.</item>
    ///   <item>DueSoonScheduleCount — 0: dueSoonSeconds=0 and the slot cursors are parked a day out, so the zero window can never include them.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact(DisplayName = "Driven state pins all overview counters to exact values in an isolated namespace")]
    public async Task Driven_state_pins_exact_overview_counters()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        const int LeaseTtl = 300; // 5 min; no heartbeat in this test

        // W1: single worker registered by Runtime.InitializeAsync in this unique namespace.
        var w1 = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(w1);

        // ── Baseline snapshot ─────────────────────────────────────────────────────────────────────
        // Runtime.InitializeAsync seeds one Ready slot job for every schedule whose environments list
        // is empty (currently: recurring-ping with "every-5-minutes"). Take a before-snapshot now so
        // ReadyCount and JobCount assertions use deltas and remain correct if the manifest changes.
        var before = await Overview.GetOverviewAsync(new OverviewQuery(TestNamespace, 3600, 0), ct);
        // Confirm the namespace is otherwise clean (fresh, unique per test-instance).
        Assert.Equal(0, before.ExecutingCount);
        Assert.Equal(0, before.FailedCount);
        Assert.Equal(0, before.UnresolvedAlertCount);
        Assert.Equal(0, before.DeadWorkerCount);
        Assert.Equal(0, before.StaleWorkerCount);
        Assert.Equal(0, before.SystemJobCount);

        // ── Job counters ───────────────────────────────────────────────────────────────────────────
        // Enqueue 4 jobs. RegisterFrameworkJobs = false → no system job definitions → SystemJobCount = 0.
        var j1 = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        var j2 = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);
        var j3 = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))), ct);
        var j4 = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(4, 5))), ct);

        // j3 → Executing: claim + StartExecution, leave running (no CompleteExecution call).
        var c3 = Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, w1!.Id, LeaseTtl, j3, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(c3.JobId, w1.Id, c3.ExecutionNumber, c3.Version, LeaseTtl, ct)
        );

        // j4 → Failed: claim + start + ctx.FailAsync-style complete (HandlerStatusCode = 200 bypasses the failure budget).
        var c4 = Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, w1.Id, LeaseTtl, j4, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(c4.JobId, w1.Id, c4.ExecutionNumber, c4.Version, LeaseTtl, ct)
        );
        var cr4 = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(
                new CompleteExecutionRequest(c4.JobId, w1.Id, c4.ExecutionNumber, ExecutionOutcome.Failed, 0, ReadOnlyMemory<byte>.Empty)
                {
                    HandlerStatusCode = (byte)JobStatusCode.Failed,
                },
                ct
            );
        Assert.Equal(CompleteExecutionAction.Completed, cr4.Action);

        // ── Alerts ────────────────────────────────────────────────────────────────────────────────
        // Two unresolved alerts on j1: one Error (severity 3) and one Critical (severity 4).
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            j1.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Error,
            AlertKindCode.Manual,
            "pin-error",
            "error body",
            "ops",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            j1.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Critical,
            AlertKindCode.Manual,
            "pin-critical",
            "critical body",
            "ops",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );

        // ── Dead worker ───────────────────────────────────────────────────────────────────────────
        // Direct-stamp W1 Dead. Using the global MarkDeadWorkers reaper is racy on the shared parallel
        // schema (a concurrent global sweep races our own); stamping the exact status keeps
        // DeadWorkerCount deterministic. DeadWorkerCount is namespace-scoped.
        await Db.From<JobWorker>().Where(w => w.Id == w1.Id).UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Dead }, ct);

        // ── Stale-worker threshold flip ───────────────────────────────────────────────────────────
        // W2 is started fresh after W1 is dead, then aged to 3 h ago.
        // staleAfterSeconds=3600 (1 h) → W2's 3 h > 1 h → stale (count = 1).
        // staleAfterSeconds=14400 (4 h) → W2's 3 h < 4 h → not stale (count = 0).
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
            maxConcurrency: 1,
            ct
        );
        // Stamp W2 Draining + stale rather than Active + stale. StaleWorkerCount counts both Active and
        // Draining workers, but the global MarkDeadWorkers reaper only sweeps Active — so a Draining stale
        // worker is still counted stale yet is immune to a concurrent global sweep that would otherwise
        // flip it Dead and inflate DeadWorkerCount (the off-by-one flake).
        await Db.From<JobWorker>()
            .Where(w => w.Id == w2Id)
            .UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Draining, LastSeenAtUtc = DateTime.UtcNow.AddHours(-3) }, ct);

        // ── Narrow-threshold assertion (staleAfterSeconds = 1 h) ─────────────────────────────────
        var ovNarrow = await Overview.GetOverviewAsync(new OverviewQuery(TestNamespace, 3600, 0), ct);

        // ReadyCount and JobCount: delta from before (before includes slot jobs seeded by InitializeAsync).
        Assert.Equal(before.ReadyCount + 2, ovNarrow.ReadyCount); // j1, j2 added; j3 Executing, j4 Failed
        Assert.Equal(1, ovNarrow.ExecutingCount); // j3 is Executing
        Assert.Equal(1, ovNarrow.FailedCount); // j4 is Failed
        Assert.Equal(2, ovNarrow.UnresolvedAlertCount); // 2 raised, none resolved
        Assert.Equal(1, ovNarrow.UnresolvedCriticalAlertCount); // 1 Critical (severity = 40)
        Assert.Equal(1, ovNarrow.DeadWorkerCount); // W1 swept Dead, scoped to TestNamespace
        Assert.Equal(1, ovNarrow.StaleWorkerCount); // W2 Active, 3 h > 1 h threshold
        Assert.Equal(0, ovNarrow.DueSoonScheduleCount); // dueSoonSeconds=0: harness parks slot cursors a day out
        Assert.Equal(before.JobCount + 4, ovNarrow.JobCount); // 4 enqueued on top of slot jobs
        Assert.Equal(0, ovNarrow.SystemJobCount); // RegisterFrameworkJobs = false, recurring-ping is not a __ job
        Assert.NotNull(ovNarrow.OldestReadyAgeSeconds);
        Assert.True(ovNarrow.OldestReadyAgeSeconds >= 0);

        // ── Wide-threshold assertion (staleAfterSeconds = 4 h): count flips to 0 ────────────────
        var ovWide = await Overview.GetOverviewAsync(new OverviewQuery(TestNamespace, 14400, 0), ct);

        Assert.Equal(0, ovWide.StaleWorkerCount); // threshold crossed: W2's 3 h < 4 h
        // Cross-check that other counters are unchanged across the threshold call.
        Assert.Equal(before.ReadyCount + 2, ovWide.ReadyCount);
        Assert.Equal(1, ovWide.ExecutingCount);
        Assert.Equal(1, ovWide.FailedCount);
        Assert.Equal(1, ovWide.DeadWorkerCount);
        Assert.Equal(2, ovWide.UnresolvedAlertCount);
        Assert.Equal(1, ovWide.UnresolvedCriticalAlertCount);
    }
}
