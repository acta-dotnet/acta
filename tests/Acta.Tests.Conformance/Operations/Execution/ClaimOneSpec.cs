using System.Diagnostics;
using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>claim_one</c>, the claim that takes one named job, or the scan's next job when it names none.</summary>
[ConformanceSpec(
    "claim-one.by-id",
    "A by-id claim takes exactly the named due job or nothing",
    Area = "Claim",
    Contract = "ClaimOne leases the named job only while it is due in the caller's namespace, and without a name leases at most one job by the scan."
)]
public sealed class ClaimOneSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int LeaseTtlSeconds = 30;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private Acta.Runtime.Execution Execution => Services.GetRequiredService<Acta.Runtime.Execution>();

    [Fact(DisplayName = "The named Ready job goes Dispatched under the worker's lease, one attempt and one version on")]
    public async Task Named_ready_job_is_dispatched_under_the_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var enqueued = await EnqueueAsync(ct);
        var before = await ReadJobAsync(enqueued.JobId, ct);

        var claimed = Assert.Single(await ClaimUntilTakenAsync(new ClaimRequest(NamespaceId, workerId, MaxBatch: 1), enqueued.JobId, ct));

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, after.Status);
        Assert.Equal(workerId, after.LeasedByWorkerId);
        Assert.True(after.LeaseExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(before.ExecutionNumber + 1, after.ExecutionNumber);
        Assert.Equal(before.Version + 1, after.Version);

        // The claim carries what the runner needs without reading jobs again, and the version is the start CAS token.
        Assert.Equal(enqueued.JobId, claimed.JobId);
        Assert.Equal(enqueued.JobRef.Value, claimed.JobRef);
        Assert.Equal(after.ExecutionNumber, claimed.ExecutionNumber);
        Assert.Equal(after.Version, claimed.Version);
        Assert.Equal(before.DefinitionId, claimed.DefinitionId);
        Assert.False(claimed.Input.IsEmpty);
        Assert.Empty(await StartedEventsAsync(enqueued.JobId, ct));
    }

    [Fact(DisplayName = "A claim that starts executing lands the job Executing, with a started event when the job is audited")]
    public async Task Start_executing_claim_lands_executing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var enqueued = await EnqueueAsync(ct);

        var claimed = Assert.Single(
            await ClaimUntilTakenAsync(new ClaimRequest(NamespaceId, workerId, MaxBatch: 1, StartExecuting: true), enqueued.JobId, ct)
        );

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.Equal(workerId, after.LeasedByWorkerId);

        var started = await StartedEventsAsync(enqueued.JobId, ct);
        if (after.AuditLevel == JobAuditLevelCode.Audit)
        {
            var startedEvent = Assert.Single(started);
            Assert.Equal(claimed.ExecutionNumber, startedEvent.ExecutionNumber);
            Assert.Equal(workerId, startedEvent.WorkerId);
            Assert.Equal(JobStatusCode.Ready, startedEvent.FromStatus);
            Assert.Equal(JobStatusCode.Executing, startedEvent.ToStatus);
        }
        else
        {
            Assert.Empty(started);
        }
    }

    [Fact(DisplayName = "A Suspended job whose wait deadline has passed is claimed, and one waiting with no deadline is not")]
    public async Task Suspended_job_is_claimed_only_past_its_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var request = new ClaimRequest(NamespaceId, workerId, MaxBatch: 1);
        var deadlinePassed = await EnqueueAsync(ct);
        var unbounded = await EnqueueAsync(ct);
        await SuspendAsync(deadlinePassed.JobId, DateTime.UtcNow.AddMinutes(-1), ct);
        await SuspendAsync(unbounded.JobId, nextRunAtUtc: null, ct);

        Assert.Equal(deadlinePassed.JobId, Assert.Single(await ClaimUntilTakenAsync(request, deadlinePassed.JobId, ct)).JobId);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(deadlinePassed.JobId, ct)).Status);

        var waiting = await ReadJobAsync(unbounded.JobId, ct);
        Assert.Empty((await Execution.ClaimOneAsync(request, LeaseTtlSeconds, unbounded.JobId, ct)).Jobs);
        await AssertUntouchedAsync(waiting, ct);
    }

    [Fact(DisplayName = "A job not yet due, paused, or named under another namespace is not claimed and keeps its version")]
    public async Task Unclaimable_jobs_are_left_as_they_were()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var request = new ClaimRequest(NamespaceId, workerId, MaxBatch: 1);

        var notDue = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                NextRunAtUtc: DateTime.UtcNow.AddHours(1)
            ),
            ct
        );
        var paused = await EnqueueAsync(ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(paused, ct: ct)).Action);
        var elsewhere = await EnqueueAsync(ct);

        foreach (
            var (jobId, claim) in new[]
            {
                (notDue.JobId, request),
                (paused.JobId, request),
                (elsewhere.JobId, request with { NamespaceId = int.MaxValue }),
            }
        )
        {
            var before = await ReadJobAsync(jobId, ct);
            Assert.Empty((await Execution.ClaimOneAsync(claim, LeaseTtlSeconds, jobId, ct)).Jobs);
            await AssertUntouchedAsync(before, ct);
        }
    }

    [Fact(DisplayName = "A job already claimed is not claimed again and keeps its first lease")]
    public async Task Claimed_job_is_not_claimed_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var enqueued = await EnqueueAsync(ct);
        Assert.Single(await ClaimUntilTakenAsync(new ClaimRequest(NamespaceId, workerId, MaxBatch: 1), enqueued.JobId, ct));
        var held = await ReadJobAsync(enqueued.JobId, ct);

        Assert.Empty(
            (await Execution.ClaimOneAsync(new ClaimRequest(NamespaceId, workerId, MaxBatch: 1), LeaseTtlSeconds, enqueued.JobId, ct)).Jobs
        );

        await AssertUntouchedAsync(held, ct);
        Assert.Equal(workerId, (await ReadJobAsync(enqueued.JobId, ct)).LeasedByWorkerId);
    }

    [Fact(DisplayName = "Without a job id the claim takes a single job by the scan, whatever batch size it was asked for")]
    public async Task Unnamed_claim_takes_one_job_by_the_scan()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var enqueued = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            enqueued.Add((await EnqueueAsync(ct)).JobId);
        }

        var claimed = Assert.Single(await ClaimUntilTakenAsync(new ClaimRequest(NamespaceId, workerId, MaxBatch: 3), jobId: null, ct));

        Assert.Contains(claimed.JobId, enqueued);
        foreach (var other in enqueued.Where(id => id != claimed.JobId))
        {
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(other, ct)).Status);
        }
    }

    private async Task<JobEnqueueOutcome> EnqueueAsync(CancellationToken ct) =>
        await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

    /// <summary>
    /// Claims until a job comes back, bounded to a short budget. The claim skips a row another connection
    /// holds locked, which on the shared schema can briefly hide a claimable one; production's next tick
    /// takes it, and so does the next call here.
    /// </summary>
    private async Task<IReadOnlyList<ClaimedJob>> ClaimUntilTakenAsync(ClaimRequest request, long? jobId, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var claimed = (await Execution.ClaimOneAsync(request, LeaseTtlSeconds, jobId, ct)).Jobs;
            if (claimed.Count > 0 || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                return claimed;
            }
            await Task.Delay(25, ct);
        }
    }

    private async Task SuspendAsync(long jobId, DateTime? nextRunAtUtc, CancellationToken ct)
    {
        var affected = await Db.From<RuntimeEntity>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(() => new RuntimeEntity { Status = JobStatusCode.Suspended, NextRunAtUtc = nextRunAtUtc }, ct);
        Assert.Equal(1, affected);
    }

    private async Task AssertUntouchedAsync(TestJobRow before, CancellationToken ct)
    {
        var after = await ReadJobAsync(before.Id, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ExecutionNumber, after.ExecutionNumber);
        Assert.Equal(before.LeasedByWorkerId, after.LeasedByWorkerId);
    }

    private Task<IReadOnlyList<EventEntity>> StartedEventsAsync(long jobId, CancellationToken ct) =>
        Db.From<EventEntity>().Where(e => e.JobId == jobId && e.EventCode == EventCode.JobExecutionStarted).ToListAsync(ct);

    private async Task<int> WorkerIdAsync(CancellationToken ct)
    {
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == NamespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
