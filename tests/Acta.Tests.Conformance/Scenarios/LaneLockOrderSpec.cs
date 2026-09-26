using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Operations that race on one lane serialize on the lane lock, which every one of them takes before any
/// job row (docs/internals/sql-execution-policy.md, "Lane lock order"). Deadlock retry is off for this
/// spec, so a deadlock fails the test instead of being absorbed by a retry. Each race claims and starts
/// the settling job first and then fires the bare completion alongside the other operation, so the two
/// statements meet. Server providers only: SQLite runs one writer at a time.
/// </summary>
[ConformanceSpec(
    "lanes.lock-order",
    "Operations racing on one lane serialize on the lane lock without deadlocking",
    Area = "Lanes",
    Contract = "Concurrent enqueues, completions, and cancels on a lane take the lane lock first, so a younger enqueue waits for an older open one and none of them deadlocks.",
    Arrange = "Laned jobs sit in a private namespace, and deadlock retry is off so a deadlock victim surfaces.",
    Act = "Open transactions, parent enqueues, completions, cancels, and batches with opposite lane orders race on shared lanes, many times over.",
    Assert = "Every operation succeeds, the older transaction's job leads its lane, and each lane ends in a consistent state."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class LaneLockOrderSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Rounds = 20;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            options.DeadlockRetryAttempts = 1;
            return options;
        });
    }

    [Fact(DisplayName = "A younger enqueue waits for an older open transaction on the same lane and lands behind it")]
    public async Task Younger_enqueue_waits_for_the_older_open_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        // The lane exists and is idle before either transaction starts, so the younger enqueue meets the
        // older one's lane lock rather than its uncommitted insert of the lane row.
        var seed = await Jobs.EnqueueAsync(Step("orders", "seed"), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(seed, ct: ct)).Action);

        await using var olderConnection = await Db.OpenConnectionAsync(ct);
        await using var older = await olderConnection.BeginTransactionAsync(ct);
        var first = await Jobs.EnqueueAsync(older, Step("orders", "older"), ct);

        await using var youngerConnection = await Db.OpenConnectionAsync(ct);
        await using var younger = await youngerConnection.BeginTransactionAsync(ct);
        var pending = Jobs.EnqueueAsync(younger, Step("orders", "younger"), ct).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        Assert.False(pending.IsCompleted, "the younger enqueue did not wait for the older transaction's lane lock");

        await older.CommitAsync(ct);
        var second = await pending;
        await younger.CommitAsync(ct);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(first.JobId, ct)).Status);
        var follower = await Jobs.GetAsync(second, ct);
        Assert.Equal(JobStatusCode.Blocked, follower!.Status);
        Assert.Equal(first.JobRef, follower.BlockedBehindJobRef);
    }

    [Fact(DisplayName = "A parent enqueuing a child into a lane never deadlocks with an older child of that lane completing")]
    public async Task Parent_enqueue_and_older_child_completion_do_not_deadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            var lane = $"family-{round}";
            var parent = await Jobs.EnqueueAsync(Step("", $"parent-{round}"), ct);
            var older = await Jobs.EnqueueAsync(Step(lane, $"older-{round}") with { ParentJobId = parent.JobId }, ct);
            var complete = await StartedAsync(older, ct);

            var completion = complete();
            var enqueue = Jobs.EnqueueAsync(Step(lane, $"younger-{round}") with { ParentJobId = parent.JobId }, ct).AsTask();
            await Task.WhenAll(completion, enqueue);

            Assert.Equal(CompleteExecutionAction.Completed, (await completion).Action);
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync((await enqueue).JobId, ct)).Status);
        }
    }

    [Fact(DisplayName = "Cancelling a Blocked follower never deadlocks with its head completing")]
    public async Task Follower_cancel_and_head_completion_do_not_deadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            var lane = $"cancel-{round}";
            var head = await Jobs.EnqueueAsync(Step(lane, $"head-{round}"), ct);
            var follower = await Jobs.EnqueueAsync(Step(lane, $"follower-{round}"), ct);
            var last = await Jobs.EnqueueAsync(Step(lane, $"last-{round}"), ct);
            var complete = await StartedAsync(head, ct);

            var completion = complete();
            var cancel = Jobs.CancelAsync(follower, "racing the head", ct: ct).AsTask();
            await Task.WhenAll(completion, cancel);

            Assert.Equal(CompleteExecutionAction.Completed, (await completion).Action);
            Assert.Equal(ControlAction.Applied, (await cancel).Action);
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(last.JobId, ct)).Status);
        }
    }

    [Fact(DisplayName = "Two enqueue batches naming shared lanes in opposite orders never deadlock with a completing head")]
    public async Task Opposite_batches_and_a_completion_do_not_deadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            // Every lane exists before the race, so both batches go straight to locking the same lane rows;
            // a lane one batch creates would instead hold the other on its uncommitted insert.
            string[] lanes = [.. Enumerable.Range(0, 8).Select(i => $"{(char)('a' + i)}-{round}")];
            var seeds = await Jobs.EnqueueBatchAsync([.. lanes.Select(l => Step(l, $"seed-{l}"))], ct);
            var complete = await StartedAsync(seeds[0], ct);

            var batches = Enumerable
                .Range(0, 4)
                .Select(b => (b % 2 == 0 ? lanes : lanes.Reverse()).Select(l => Step(l, $"batch{b}-{l}")).ToList())
                .Select(batch => Jobs.EnqueueBatchAsync(batch, ct).AsTask())
                .ToList();
            var completion = complete();
            await Task.WhenAll([.. batches, completion]);

            Assert.Equal(CompleteExecutionAction.Completed, (await completion).Action);
            foreach (var lane in lanes)
            {
                var page = await Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Lane: lane), ct);
                var unfinished = page
                    .Items.Where(i => i.Status is JobStatusCode.Ready or JobStatusCode.Blocked)
                    .OrderBy(i => i.JobId)
                    .ToList();
                Assert.Equal(JobStatusCode.Ready, unfinished[0].Status);
                Assert.All(unfinished.Skip(1), i => Assert.Equal(JobStatusCode.Blocked, i.Status));
            }
        }
    }

    // Claims and starts the job as this namespace's worker and hands back its completion, unsent.
    private async Task<Func<Task<CompleteExecutionResult>>> StartedAsync(JobEnqueueOutcome job, CancellationToken ct)
    {
        var store = Services.GetRequiredService<IExecutionStore>();
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        var workerId = Assert.IsType<JobWorker>(worker).Id;
        var claimed = Assert.Single(await store.ClaimOneAsync(ns, workerId, leaseTtl, job.JobId, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await store.StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );
        return () =>
            store.CompleteExecutionAsync(
                new CompleteExecutionRequest(
                    JobId: claimed.JobId,
                    WorkerId: workerId,
                    ExpectedExecutionNumber: claimed.ExecutionNumber,
                    Outcome: ExecutionOutcome.Succeeded,
                    ResultFormatId: 0,
                    Result: ReadOnlyMemory<byte>.Empty
                ),
                ct
            );
    }

    private JobEnqueueRequest Step(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane.Length == 0 ? null : lane);
}
