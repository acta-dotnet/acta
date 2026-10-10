using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for <c>start_execution</c>. The start matches the claim on job, worker, and execution number: a claim
/// the row still carries starts, even past a lapsed lease recovery has not taken; a start replayed after it committed
/// and lost its answer is answered Started again without a second started event or version bump; and a claim the
/// row has moved past is refused as <c>LostClaim</c>.
/// </summary>
[ConformanceSpec(
    "start-execution.claim-match",
    "Start execution matches the claim on worker and execution number",
    Area = "Execution",
    Contract = "Start moves this worker's claim at its execution to Executing, answers a replay Started without a second event, and refuses another execution as LostClaim."
)]
public sealed class StartExecutionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A claim starts, and a replayed start is answered Started without a second event or version bump")]
    public async Task A_claim_starts_and_a_replayed_start_is_answered_started_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (claimed, workerId, leaseTtl) = await ClaimAsync(leaseTtlSeconds: null, ct);
        var execution = Services.GetRequiredService<Acta.Execution>();

        var first = await execution.StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, leaseTtl, ct);
        var started = await ReadJobAsync(claimed.JobId, ct);
        var replay = await execution.StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, leaseTtl, ct);

        Assert.Equal(StartExecutionAction.Started, first.Action);
        Assert.Equal(StartExecutionAction.Started, replay.Action);
        var row = await ReadJobAsync(claimed.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, row.Status);
        Assert.Equal(claimed.Version + 1, started.Version);
        Assert.Equal(started.Version, row.Version);
        Assert.Single(await StartedEventsAsync(claimed.JobId, ct));
    }

    [Fact(DisplayName = "A start for an execution the row has moved past is refused as LostClaim and the job stays Dispatched")]
    public async Task A_start_for_another_execution_is_refused_as_lost_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var (claimed, workerId, leaseTtl) = await ClaimAsync(leaseTtlSeconds: null, ct);

        var action = (
            await Services
                .GetRequiredService<Acta.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber - 1, leaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.LostClaim, action);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(claimed.JobId, ct)).Status);
        Assert.Empty(await StartedEventsAsync(claimed.JobId, ct));
    }

    [Fact(DisplayName = "A claim whose lease lapsed but was not reclaimed starts, and the start renews its lease")]
    public async Task A_claim_past_its_lease_still_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        // A negative TTL lands the lease in the past: the worker buffered this claim past its lease, and
        // recovery has not taken the row, so it still carries this worker at this execution.
        var (claimed, workerId, leaseTtl) = await ClaimAsync(leaseTtlSeconds: -5, ct);

        var action = (
            await Services
                .GetRequiredService<Acta.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, leaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.Started, action);
        var row = await ReadJobAsync(claimed.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, row.Status);
        Assert.True(row.LeaseExpiresAtUtc > claimed.LeaseExpiresAtUtc, "the start did not renew the lapsed lease");
    }

    private async Task<(ClaimedJob Claimed, int WorkerId, int LeaseTtl)> ClaimAsync(int? leaseTtlSeconds, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var claimed = Assert.Single(
            await Services.GetRequiredService<Acta.Execution>().ClaimJobAsync(ns, workerId, leaseTtlSeconds ?? leaseTtl, enqueued, ct)
        );
        return (claimed, workerId, leaseTtl);
    }

    private Task<IReadOnlyList<EventEntity>> StartedEventsAsync(long jobId, CancellationToken ct) =>
        Db.From<EventEntity>().Where(e => e.JobId == jobId && e.EventCode == EventCode.JobExecutionStarted).ToListAsync(ct);
}
