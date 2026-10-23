using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

internal static class ChaosSpecHelpers
{
    public static async Task<int> NamespaceIdAsync(IDbSession db, string name, CancellationToken ct)
    {
        var row = await db.From<NamespaceEntity>().Where(n => n.NamespaceName == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.Id;
    }

    public static async Task<int> WorkerIdAsync(IDbSession db, int namespaceId, CancellationToken ct)
    {
        var rows = await db.From<WorkerEntity>().Where(w => w.NamespaceId == namespaceId).ToListAsync(ct);
        return Assert.Single(rows).Id;
    }

    public static async Task ExpireLeaseAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var affected = await db.From<JobStateEntity>()
            .Where(r => r.Id == jobId && r.LeasedByWorkerId != null)
            .UpdateOnlyAsync(() => new JobStateEntity { LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5) }, ct);
        Assert.Equal(1, affected);
    }

    /// <summary>
    /// Leaves the job as a worker that died mid-handler leaves it: claimed straight to Executing under a
    /// lease already lapsed. Recovery charges this attempt; a claim that never started is reclaimed uncharged.
    /// Answers the fence the lost attempt's writes carried.
    /// </summary>
    public static async Task<ExecutionFence> StrandExecutingAsync(
        IServiceProvider services,
        int namespaceId,
        int workerId,
        long jobId,
        CancellationToken ct
    )
    {
        var claim = await services
            .GetRequiredService<ExecutionOperations>()
            .ClaimJobAsync(new ClaimOptions(namespaceId, workerId, MaxBatch: 1), leaseTtlSeconds: -5, jobId, ct);
        var claimed = Assert.Single(claim.Jobs);
        Assert.Equal(jobId, claimed.JobId);
        return new ExecutionFence(jobId, claimed.ExecutionNumber, workerId);
    }

    public static async Task SetReadyAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var affected = await db.From<JobStateEntity>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(
                () =>
                    new JobStateEntity
                    {
                        Status = JobStatusCode.Ready,
                        NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
                        LeasedByWorkerId = null,
                        LeaseExpiresAtUtc = null,
                    },
                ct
            );
        Assert.Equal(1, affected);
    }

    public static async Task<EnqueueJobResponse> EnqueueNoPayloadAsync(IJobs jobs, string ns, string jobName, CancellationToken ct) =>
        await jobs.EnqueueAsync(new EnqueueJobRequest(ns, jobName, JobPayload.None), ct);

    public static async Task<EnqueueJobResponse> EnqueueAddNumbersAsync(
        IServiceProvider services,
        IJobs jobs,
        string ns,
        CancellationToken ct
    )
    {
        var serializers = services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TestJobs.AddNumbers(2, 3));
        return await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                JobNamespace: ns,
                JobName: "add-numbers",
                Input: payload,
                DeduplicationKey: $"chaos-{Guid.NewGuid()}",
                CorrelationKey: null,
                Priority: null
            ),
            ct
        );
    }

    public static async Task<int> ReclaimAsync(IServiceProvider services, int namespaceId, CancellationToken ct) =>
        (await RecoverySweep.ReclaimAtLeastOneAsync(services, namespaceId, ct)).Reclaimed;

    /// <summary>
    /// The one lease-loss event of the timeline: orphaned with its landing in details, or a terminal failure when the
    /// reclaim spent the budget.
    /// </summary>
    public static JobEventRecord AssertRecoveryEvent(IReadOnlyList<JobEventRecord> events, JobStatusCode to)
    {
        var match = Assert.Single(
            events.Where(e => e.EventCode is EventCode.JobExecutionOrphaned or EventCode.JobFailed && e.Reason == "lease-expired")
        );
        if (to == JobStatusCode.Failed)
        {
            Assert.Equal(EventCode.JobFailed, match.EventCode);
        }
        else
        {
            Assert.Equal(EventCode.JobExecutionOrphaned, match.EventCode);
            Assert.Equal(to.Code, match.To);
        }
        return match;
    }

    /// <summary>The one attempt event of the given code.</summary>
    public static JobEventRecord AssertSingleAttempt(IReadOnlyList<JobEventRecord> events, EventCode code) =>
        Assert.Single(events.Where(e => e.EventCode == code));

    /// <summary>
    /// Stages the one way a completion is lost now that the write repeats until it lands: the store keeps
    /// refusing it and the worker stops mid-repeat. Returns once the run has ended with the write's own
    /// failure and the fault is cleared, leaving the row Executing for recovery.
    /// </summary>
    public static async Task StopWorkerMidCompletionRetryAsync(
        WorkerRuntime runtime,
        StoreFaultPlan faults,
        EnqueueJobResponse enqueued,
        CancellationToken ct
    )
    {
        faults.ThrowBeforeCompleteUntilCleared();
        using var worker = new CancellationTokenSource();
        var run = runtime.RunOnceAsync(enqueued, worker.Token);
        while (faults.CompletionRefusals == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }

        await worker.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        faults.StopThrowingBeforeComplete();
    }
}
