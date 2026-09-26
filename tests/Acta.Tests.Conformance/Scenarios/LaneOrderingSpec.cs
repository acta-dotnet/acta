using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Lane ordering through the runtime: the jobs of one lane run one at a time in job-id order, a laned
/// job waits as Blocked until every older member of its lane has finished, and each settle promotes
/// the next member. Each test owns a private namespace with system jobs off, so only its own rows are
/// ever claimable, and ticks run one claim at a time.
/// </summary>
[ConformanceSpec(
    "lanes.ordering",
    "A lane runs its jobs one at a time in enqueue order",
    Area = "Lanes",
    Contract = "Jobs of one lane run one at a time in job-id order, each settle promoting the next unfinished member, and no retry, delay, or priority reorders them.",
    Arrange = "Laned probe jobs are enqueued singly and in batches into a private namespace, with heads that retry, fail for good, carry a delay, or are cancelled.",
    Act = "The runtime drains the namespace one tick at a time, and operators cancel heads and followers.",
    Assert = "Each lane's probes ran in enqueue order, only the lane head was ever Ready, and each settle made the next unfinished member the head."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
public abstract class LaneOrderingSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A lane runs its jobs in enqueue order, one at a time, whatever their priority")]
    public async Task Lane_runs_in_enqueue_order_whatever_the_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await EnqueueStepAsync("orders", "a", JobPriorityCode.Bulk, ct);
        var b = await EnqueueStepAsync("orders", "b", JobPriorityCode.High, ct);
        var c = await EnqueueStepAsync("orders", "c", JobPriorityCode.Critical, ct);
        var other = await EnqueueStepAsync("billing", "x", JobPriorityCode.Normal, ct);

        Assert.Equal(JobStatusCode.Ready, await StatusAsync(a, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(b, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(c, ct));
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(other, ct));

        await DrainAsync([a, b, c, other], ct);

        Assert.Equal(["a", "b", "c"], LaneProbes.Ran(TestNamespace).Where(l => l != "x"));
        Assert.Equal(1, LaneProbes.MaxConcurrent(TestNamespace));
    }

    [Fact(DisplayName = "A retrying head holds its lane until it succeeds")]
    public async Task Retrying_head_holds_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await EnqueueAsync("lane-flaky", new LaneFlakyStep("orders", "f", Failures: 2), "orders", ct);
        var next = await EnqueueStepAsync("orders", "b", JobPriorityCode.Normal, ct);

        await DrainAsync([head, next], ct);

        Assert.Equal(["f#1", "f#2", "f#3", "b"], LaneProbes.Ran(TestNamespace));
        Assert.Equal(JobStatusCode.Succeeded, await StatusAsync(head, ct));
    }

    [Fact(DisplayName = "A head that fails for good promotes the next member")]
    public async Task Failed_head_promotes_the_next_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await EnqueueAsync("lane-doomed", new LaneDoomedStep("orders", "d"), "orders", ct);
        var next = await EnqueueStepAsync("orders", "b", JobPriorityCode.Normal, ct);

        await DrainAsync([head, next], ct);

        Assert.Equal(["d", "d", "b"], LaneProbes.Ran(TestNamespace));
        Assert.Equal(JobStatusCode.Failed, await StatusAsync(head, ct));
        Assert.Equal(JobStatusCode.Succeeded, await StatusAsync(next, ct));
    }

    [Fact(DisplayName = "Cancelling a follower promotes nothing; cancelling the head promotes the next live member")]
    public async Task Cancel_of_a_follower_and_of_the_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await EnqueueStepAsync("orders", "a", JobPriorityCode.Normal, ct);
        var b = await EnqueueStepAsync("orders", "b", JobPriorityCode.Normal, ct);
        var c = await EnqueueStepAsync("orders", "c", JobPriorityCode.Normal, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(b, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(a, ct));
        Assert.Equal(JobStatusCode.Cancelled, await StatusAsync(b, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(c, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(a, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(c, ct));

        await DrainAsync([c], ct);
        Assert.Equal(["c"], LaneProbes.Ran(TestNamespace));
    }

    [Fact(DisplayName = "A delayed head blocks newer members, which stay out of the claim and its horizon")]
    public async Task Delayed_head_blocks_newer_members()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "a"), "orders") with { DelaySeconds = 3600 }, ct);
        var next = await EnqueueStepAsync("orders", "b", JobPriorityCode.Normal, ct);

        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(next, ct));
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, ct));
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, next.JobId, ct));

        // The Blocked member is due now by its own run time, so a horizon that counted it would sit at
        // or before db_now and spin the claim loop at the anti-spin floor.
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<Acta.Relational.Entities.JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var workerId = worker!.Id;
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claim = await Services
            .GetRequiredService<IExecutionStore>()
            .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: 10), leaseTtl, ct);

        Assert.Empty(claim.Jobs);
        var horizon = Assert.NotNull(claim.Horizon);
        Assert.True(
            horizon.NextReadyAtUtc > horizon.DbNowUtc.AddMinutes(30),
            $"horizon {horizon.NextReadyAtUtc:O} counted the Blocked member"
        );
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(head, ct));
    }

    [Fact(DisplayName = "A batch lands its lane members in batch order")]
    public async Task Batch_lands_members_in_batch_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcomes = await Jobs.EnqueueBatchAsync(
            [
                Request("lane-step", new LaneStep("orders", "p1"), "orders"),
                Request("lane-step", new LaneStep("billing", "q"), "billing"),
                Request("lane-step", new LaneStep("orders", "p2"), "orders"),
                Request("lane-step", new LaneStep("orders", "p3"), "orders"),
            ],
            ct
        );

        Assert.Equal(outcomes.Select(o => o.JobId).Order(), outcomes.Select(o => o.JobId));
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(outcomes[0], ct));
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(outcomes[1], ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(outcomes[2], ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(outcomes[3], ct));

        await DrainAsync(outcomes, ct);
        Assert.Equal(["p1", "p2", "p3"], LaneProbes.Ran(TestNamespace).Where(l => l != "q"));
    }

    [Fact(DisplayName = "A batch whose first lane row is a dedup hit on a finished job lands its next row Ready")]
    public async Task Batch_after_a_dedup_hit_on_a_finished_member_lands_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var finished = await Jobs.EnqueueAsync(
            Request("lane-step", new LaneStep("orders", "a"), "orders") with
            {
                DeduplicationKey = "a",
            },
            ct
        );
        await DrainAsync([finished], ct);

        var outcomes = await Jobs.EnqueueBatchAsync(
            [
                Request("lane-step", new LaneStep("orders", "a"), "orders") with
                {
                    DeduplicationKey = "a",
                },
                Request("lane-step", new LaneStep("orders", "b"), "orders"),
                Request("lane-step", new LaneStep("orders", "c"), "orders"),
            ],
            ct
        );

        Assert.Equal(JobEnqueueAction.Deduplicated, outcomes[0].Action);
        Assert.Equal(finished.JobId, outcomes[0].JobId);
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(outcomes[1], ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(outcomes[2], ct));
    }

    [Fact(DisplayName = "Concurrent producers into one lane: the lane runs in job-id order")]
    public async Task Concurrent_producers_run_in_job_id_order()
    {
        var ct = TestContext.Current.CancellationToken;
        const int producers = 8;

        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, producers)
                .Select(i => Task.Run(async () => await EnqueueStepAsync("orders", $"p{i}", JobPriorityCode.Normal, ct), ct))
        );

        var statuses = await StatusesAsync(outcomes, ct);
        Assert.Single(statuses, s => s == JobStatusCode.Ready);
        Assert.Equal(outcomes.MinBy(o => o.JobId)!.JobId, outcomes[statuses.IndexOf(JobStatusCode.Ready)].JobId);

        await DrainAsync(outcomes, ct);

        var byId = outcomes.Select((o, i) => (o.JobId, Label: $"p{i}")).OrderBy(x => x.JobId).Select(x => x.Label);
        Assert.Equal(byId, LaneProbes.Ran(TestNamespace));
        Assert.Equal(1, LaneProbes.MaxConcurrent(TestNamespace));
    }

    [Fact(DisplayName = "Lane names fold like concurrency keys: names that differ only in case are one lane")]
    public async Task Lane_names_fold_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var upper = await EnqueueStepAsync("Customer-42", "u", JobPriorityCode.Normal, ct);
        var lower = await EnqueueStepAsync("customer-42", "l", JobPriorityCode.Normal, ct);

        Assert.Equal(JobStatusCode.Ready, await StatusAsync(upper, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(lower, ct));
    }

    [Fact(DisplayName = "A lane outside the key alphabet is refused at enqueue")]
    public async Task Invalid_lane_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await EnqueueStepAsync("orders 42", "x", JobPriorityCode.Normal, ct));
    }

    [Fact(DisplayName = "A definition lane orders its jobs, and an enqueue lane overrides it")]
    public async Task Definition_lane_orders_and_enqueue_lane_overrides()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Jobs.EnqueueAsync(Request("lane-defined", new LaneDefinedStep("d1"), lane: null), ct);
        var second = await Jobs.EnqueueAsync(Request("lane-defined", new LaneDefinedStep("d2"), lane: null), ct);
        var elsewhere = await Jobs.EnqueueAsync(Request("lane-defined", new LaneDefinedStep("e"), "elsewhere"), ct);

        Assert.Equal(JobStatusCode.Ready, await StatusAsync(first, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(second, ct));
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(elsewhere, ct));

        await DrainAsync([first, second, elsewhere], ct);
        Assert.Equal(["d1", "d2"], LaneProbes.Ran(TestNamespace).Where(l => l != "e"));
    }

    private JobEnqueueRequest Request<TInput>(string jobName, TInput input, string? lane)
        where TInput : notnull => new(TestNamespace, jobName, JobPayload.Json(input), Lane: lane);

    private async Task<JobEnqueueOutcome> EnqueueAsync<TInput>(string jobName, TInput input, string lane, CancellationToken ct)
        where TInput : notnull => await Jobs.EnqueueAsync(Request(jobName, input, lane), ct);

    private async Task<JobEnqueueOutcome> EnqueueStepAsync(string lane, string label, JobPriorityCode priority, CancellationToken ct) =>
        await Jobs.EnqueueAsync(Request("lane-step", new LaneStep(lane, label), lane) with { Priority = priority }, ct);

    private async Task<JobStatusCode> StatusAsync(JobEnqueueOutcome outcome, CancellationToken ct) =>
        (await ReadJobAsync(outcome.JobId, ct)).Status;

    private async Task<List<JobStatusCode>> StatusesAsync(IReadOnlyList<JobEnqueueOutcome> jobs, CancellationToken ct)
    {
        var statuses = new List<JobStatusCode>(jobs.Count);
        foreach (var job in jobs)
        {
            statuses.Add(await StatusAsync(job, ct));
        }

        return statuses;
    }

    // One claim per tick, until every listed job is terminal: a lane that promoted nothing would leave a
    // member Blocked and run the tick budget out.
    private async Task DrainAsync(IReadOnlyList<JobEnqueueOutcome> jobs, CancellationToken ct)
    {
        for (var tick = 0; tick < 60; tick++)
        {
            var statuses = await StatusesAsync(jobs, ct);
            if (statuses.All(s => s.IsTerminal))
            {
                return;
            }

            if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceOutcome.NothingClaimed)
            {
                await Task.Delay(25, ct);
            }
        }

        var final = await StatusesAsync(jobs, ct);
        Assert.Fail($"Lane did not drain: [{string.Join(", ", final)}]");
    }
}
