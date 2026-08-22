using Acta.Relational.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

internal static class ChaosSpecHelpers
{
    public static async Task<int> NamespaceIdAsync(IDbSession db, string name, CancellationToken ct)
    {
        var row = await db.From<JobNamespace>().Where(n => n.Name == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.Id;
    }

    public static async Task<int> WorkerIdAsync(IDbSession db, int namespaceId, CancellationToken ct)
    {
        var rows = await db.From<JobWorker>().Where(w => w.NamespaceId == namespaceId).ToListAsync(ct);
        return Assert.Single(rows).Id;
    }

    public static async Task ExpireLeaseAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var affected = await db.From<JobRuntime>()
            .Where(r => r.Id == jobId && r.LeasedByWorkerId != null)
            .UpdateOnlyAsync(() => new JobRuntime { LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5) }, ct);
        Assert.Equal(1, affected);
    }

    public static async Task SetReadyAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var affected = await db.From<JobRuntime>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(
                () =>
                    new JobRuntime
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

    public static async Task<JobEnqueueOutcome> EnqueueNoPayloadAsync(IJobs jobs, string ns, string jobName, CancellationToken ct) =>
        await jobs.EnqueueAsync(new JobEnqueueRequest(ns, jobName, JobPayload.None), ct);

    public static async Task<JobEnqueueOutcome> EnqueueAddNumbersAsync(
        IServiceProvider services,
        IJobs jobs,
        string ns,
        CancellationToken ct
    )
    {
        var serializers = services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TestJobs.AddNumbers(2, 3));
        return await jobs.EnqueueAsync(
            new JobEnqueueRequest(
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

    public static JobEventRecord AssertRecoveryEvent(IReadOnlyList<JobEventRecord> events, JobStatusCode? from, JobStatusCode? to)
    {
        var match = Assert.Single(
            events.Where(e => e.EventCode == EventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Orphaned)
        );
        Assert.Equal(from, match.FromStatus);
        Assert.Equal(to, match.ToStatus);
        Assert.Equal(JobEventReasonCode.JobLeaseExpired, match.JobEventReasonCode);
        return match;
    }

    public static JobEventRecord AssertSingleFinished(
        IReadOnlyList<JobEventRecord> events,
        ExecutionStatusCode status,
        JobStatusCode? from,
        JobStatusCode? to
    )
    {
        var match = Assert.Single(events.Where(e => e.EventCode == EventCode.JobExecutionFinished && e.ExecutionStatus == status));
        Assert.Equal(from, match.FromStatus);
        Assert.Equal(to, match.ToStatus);
        return match;
    }
}
