using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// An application's open transaction holds rows Acta has not seen committed: a laned enqueue in a new lane, due now, and
/// a child enqueued under a parent. Acta's own passes and its dashboard reads skip those rows, as read-committed snapshot
/// hides them, so none of them waits for the application to commit, whether that option is on or off.
/// </summary>
[ConformanceSpec(
    "caller-transaction.never-waited-on",
    "Acta never waits on an application's open transaction",
    Area = "Enqueue",
    Contract = "Every engine pass and dashboard read returns while an application's transaction holds an uncommitted laned enqueue and child."
)]
[Deploys("add-numbers", "echo", "lane-step")]
public sealed class OpenCallerTransactionSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<OpenCallerTransactionSpec>>
{
    protected override bool OwnSchema => true;

    [Fact(DisplayName = "An idle claim, recovery, retention, alerts, a retire, and the dashboard reads return while an enqueue is open")]
    public async Task Nothing_waits_on_an_open_enqueue()
    {
        Assert.SkipWhen(Db.Provider == DbProvider.Sqlite, "SQLite runs one writer at a time, so an open transaction holds every writer.");
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        // Due in an hour, so the idle claim below takes nothing and reads the horizon, where the open enqueue is the head.
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "echo", JobPayload.Json(new Echo("parent")), DelaySeconds: 3600),
            ct
        );
        var retired = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);

        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var application = await connection.BeginTransactionAsync(ct);
        var lane = $"open-{TestId}";
        await Jobs.EnqueueAsync(
            application,
            new EnqueueJobRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, "open")), Lane: lane),
            ct
        );
        await Jobs.EnqueueAsync(
            application,
            new EnqueueJobRequest(TestNamespace, "echo", JobPayload.Json(new Echo("child")), ParentJobId: parent.JobId),
            ct
        );

        var execution = Services.GetRequiredService<ExecutionOperations>();
        await PromptlyAsync("an idle claim", () => Runtime.RunOnceAsync(TestNamespace, ct));
        await PromptlyAsync("a recovery pass", () => execution.ReclaimStuckJobsAsync(ns, ct));
        await PromptlyAsync("the stale child latch read", () => execution.ListStaleJobChildLatchesAsync(ns, ct));
        await PromptlyAsync("a retention pass", () => RetentionTestOps.PurgeAsync(Services, ns, 7, 30, 100_000_000, 1000, 50, ct));
        await PromptlyAsync("an alerts tick", () =>
            AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, ns, parent.JobId, options: null, drain: null, ct)
        );
        await PromptlyAsync("a retire", async () =>
            await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", retired!.Version, ct: ct)
        );
        await PromptlyAsync("ListJobs", async () =>
            await Operations.Ledger.ListJobsAsync(new ListJobsQuery(TestNamespace, IncludeTotal: true, Lane: lane), ct)
        );
        await PromptlyAsync("GetOverview", async () => await Operations.Ledger.GetOverviewAsync(new GetOverviewQuery(TestNamespace), ct));
        await PromptlyAsync("the parent's lineage map", async () => await Jobs.GetLineageMapAsync(JobLookup.ById(parent.JobId), ct: ct));

        await application.RollbackAsync(ct);
    }

    /// <summary>Runs <paramref name="act"/> and fails, naming it, when it does not return inside the gate.</summary>
    private static async Task PromptlyAsync(string what, Func<Task> act)
    {
        var task = act();
        var finished = await Task.WhenAny(task, Task.Delay(SpecWaits.Gate, TestContext.Current.CancellationToken));
        Assert.True(finished == task, $"{what} waited on the application's open transaction");
        await task;
    }
}
