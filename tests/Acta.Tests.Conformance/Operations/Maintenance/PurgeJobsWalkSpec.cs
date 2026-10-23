using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for the jobs walk of <c>purge_expired_data</c>: with no retention index, the jobs section reads
/// <c>jobs_state</c> downward from a cursor, ten batches a slice, and deletes the namespace's expired jobs in it. A call
/// that deletes a full batch answers its last delete as the next cursor, and the walk answers zero at the oldest row.
/// The cutoff sits a minute past the database clock, so a zero-retention job stamped a fraction of a millisecond
/// after its completion is always expired; the shared schema holds other specs' rows, which the walk reads past.
/// </summary>
[ConformanceSpec(
    "purge-expired-data.jobs-walk",
    "Purge walks jobs_state downward from its cursor and wraps at the oldest row",
    Area = "Retention",
    Contract = "The jobs walk deletes only its namespace's expired jobs below the cursor, answers a full batch's last delete as the next cursor, and zero at the oldest row."
)]
public sealed class PurgeJobsWalkSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A full batch answers its last delete as the next cursor")]
    public async Task A_full_batch_answers_its_last_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var jobId = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("walk-full"), ct)).JobId;

        // The row right below the cursor is this job, so a one-row batch takes it and stops there.
        var walked = await WalkAsync(ns, batchSize: 1, beforeJobId: jobId + 1, ct);

        Assert.Equal(1, walked.Purged);
        Assert.Equal(jobId, walked.NextJobId);
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A walk from the newest row deletes the namespace's expired jobs and wraps at the oldest row")]
    public async Task A_walk_deletes_the_namespace_expired_jobs_and_wraps()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        long[] expired =
        [
            (await EnqueueAndRunAsync("purge-now", new PurgeProbe("walk-1"), ct)).JobId,
            (await EnqueueAndRunAsync("purge-now", new PurgeProbe("walk-2"), ct)).JobId,
            (await EnqueueAndRunAsync("purge-now", new PurgeProbe("walk-3"), ct)).JobId,
        ];
        var live = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 2), ct)).JobId;

        var cursor = 0L;
        var purged = 0;
        for (var call = 0; call < 10_000; call++)
        {
            var walked = await WalkAsync(ns, batchSize: 1000, beforeJobId: cursor, ct);
            purged += walked.Purged;
            Assert.True(cursor == 0 || walked.NextJobId < cursor, "the walk only moves down");
            cursor = walked.NextJobId;
            if (cursor == 0)
            {
                break;
            }
        }

        Assert.Equal(0, cursor);
        Assert.Equal(expired.Length, purged);
        foreach (var jobId in expired)
        {
            Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        }
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == live).SingleOrDefaultAsync(ct));
    }

    private async Task<PurgeExpiredDataResponse> WalkAsync(int ns, int batchSize, long beforeJobId, CancellationToken ct)
    {
        var now = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        return await Services
            .GetRequiredService<MaintenanceOperations>()
            .PurgeExpiredDataAsync(new PurgeExpiredDataCommand(ns, RetentionSection.Jobs, now.AddMinutes(1), batchSize, beforeJobId), ct);
    }
}
