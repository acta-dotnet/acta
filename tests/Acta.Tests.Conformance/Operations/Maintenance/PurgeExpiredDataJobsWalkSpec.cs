using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for the jobs walk of <c>purge_expired_data</c>: with no retention index, the jobs section reads its own
/// namespace's jobs from newer to older, ten batches a slice, and deletes the expired ones in it. A cycle starts at the
/// newest job old enough to have expired under the namespace's shortest retention and ends at its oldest job. Each fact
/// seeds a namespace of its own, with finished jobs created and expiring at chosen instants, so the walk's bound and
/// order are the facts' own; the cutoff sits a minute past the database clock.
/// </summary>
[ConformanceSpec(
    "purge-expired-data.jobs-walk",
    "Purge walks its namespace's jobs from the retention bound to the oldest",
    Area = "Retention",
    Contract = "The jobs walk reads only its namespace's jobs, from the newest old enough to have expired to the oldest, and deletes the expired ones a batch at a time."
)]
public sealed class PurgeExpiredDataJobsWalkSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact(DisplayName = "A cycle deletes the namespace's expired jobs, keeps the others, and ends at its oldest job")]
    public async Task A_cycle_deletes_the_expired_jobs_and_ends_at_the_oldest()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(retention: Hour, ct);
        long[] expired =
        [
            await SeedFinishedAsync(ns, definition, now - (4 * Hour), now - (3 * Hour), ct),
            await SeedFinishedAsync(ns, definition, now - (3 * Hour), now - (2 * Hour), ct),
            await SeedFinishedAsync(ns, definition, now - (2 * Hour), now - Hour, ct),
        ];
        var kept = await SeedFinishedAsync(ns, definition, now - (3.5 * Hour), now + Hour, ct);

        RetentionCursor? cursor = null;
        var purged = 0;
        for (var call = 0; call < 100; call++)
        {
            var walked = await WalkAsync(ns, batchSize: 1000, cursor, ct);
            purged += walked.Purged;
            Assert.True(cursor is null || walked.Next is null || IsOlder(walked.Next, cursor), "the walk only moves to older jobs");
            cursor = walked.Next;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);
        Assert.Equal(expired.Length, purged);
        Assert.Equal([kept], await JobsOfAsync(ns, ct));
    }

    [Fact(DisplayName = "A full batch ends its slice at its last delete, and the next call reads on from there")]
    public async Task A_full_batch_ends_its_slice_at_its_last_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(retention: Hour, ct);
        var newest = await SeedFinishedAsync(ns, definition, now - (2 * Hour), now - Hour, ct);
        var middle = await SeedFinishedAsync(ns, definition, now - (3 * Hour), now - Hour, ct);
        var oldest = await SeedFinishedAsync(ns, definition, now - (4 * Hour), now - Hour, ct);

        // One job a batch, ten a slice: the three share a slice, and each full batch stops the walk at the job it deleted,
        // so no call reads a deleted job again.
        var first = await WalkAsync(ns, batchSize: 1, before: null, ct);
        var second = await WalkAsync(ns, batchSize: 1, first.Next, ct);

        Assert.Equal((1, newest), (first.Purged, first.Next?.JobId));
        Assert.Equal((1, middle), (second.Purged, second.Next?.JobId));
        Assert.Equal([oldest], await JobsOfAsync(ns, ct));
    }

    [Fact(DisplayName = "An expired job is deleted by the first slice however many newer jobs other namespaces hold")]
    public async Task Other_namespaces_jobs_are_never_walked()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(retention: Hour, ct);
        var expired = await SeedFinishedAsync(ns, definition, now - (2 * Hour), now - Hour, ct);
        var (other, otherDefinition) = await SeedNamespaceAsync(retention: Hour, ct);
        for (var job = 0; job < 30; job++)
        {
            await SeedFinishedAsync(other, otherDefinition, now - TimeSpan.FromMinutes(1), now + Hour, ct);
        }

        // A slice is ten jobs here, and thirty newer ones of another namespace stand between the newest row and this one.
        var walked = await WalkAsync(ns, batchSize: 1, before: null, ct);

        Assert.Equal(1, walked.Purged);
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == expired).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A cycle starts at the shortest retention any definition has, a retired one included, and a younger job waits")]
    public async Task A_cycle_starts_at_the_shortest_retention_of_the_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, kept) = await SeedNamespaceAsync(retention: TimeSpan.FromDays(1), ct);
        var retired = await SeedDefinitionAsync(ns, retention: Hour, DefinitionStatusCode.Retired, ct);
        await SeedFinishedAsync(ns, retired, now - (2 * Hour), now - Hour, ct);
        // Its deadline has passed, as after an operator raised the retention it landed under, and it is younger than
        // the shortest retention: the walk does not reach it until it is that old. Late, never early.
        var young = await SeedFinishedAsync(ns, kept, now - TimeSpan.FromMinutes(30), now - TimeSpan.FromMinutes(1), ct);

        RetentionCursor? cursor = null;
        do
        {
            cursor = (await WalkAsync(ns, batchSize: 1000, cursor, ct)).Next;
        } while (cursor is not null);

        Assert.Equal([young], await JobsOfAsync(ns, ct));
    }

    private static bool IsOlder(RetentionCursor next, RetentionCursor than) =>
        next.Ticks < than.Ticks || (next.Ticks == than.Ticks && next.JobId < than.JobId);

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<IEnumerable<long>> JobsOfAsync(int ns, CancellationToken ct) =>
        (await Db.From<JobEntity>().Where(j => j.NamespaceId == ns).ToListAsync(ct)).Select(j => j.Id);

    private async Task<PurgeExpiredDataResponse> WalkAsync(int ns, int batchSize, RetentionCursor? before, CancellationToken ct) =>
        await Services
            .GetRequiredService<MaintenanceOperations>()
            .PurgeExpiredDataAsync(
                new PurgeExpiredDataCommand(ns, RetentionSection.Jobs, (await NowAsync(ct)).AddMinutes(1), batchSize, before),
                ct
            );

    private async Task<(int NamespaceId, int DefinitionId)> SeedNamespaceAsync(TimeSpan retention, CancellationToken ct)
    {
        var name = Unique("walk") + "-" + Guid.NewGuid().ToString("N")[..8];
        var ns = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(name, ct: ct);
        return (ns, await SeedDefinitionAsync(ns, retention, DefinitionStatusCode.Active, ct));
    }

    private async Task<int> SeedDefinitionAsync(int ns, TimeSpan retention, DefinitionStatusCode status, CancellationToken ct)
    {
        var name = Unique("walk-def") + "-" + status.ToString().ToLowerInvariant();
        var definition = await new ActaTestSeeder(Db).SeedJobDefinitionAsync(ns, name, ct: ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.definitions SET retention_seconds = @p_retention, definition_status_code = @p_status WHERE definition_id = @p_id",
            ct,
            ("@p_retention", (int)retention.TotalSeconds),
            ("@p_status", (int)status),
            ("@p_id", definition)
        );
        return definition;
    }

    // A finished job as its two rows, created and expiring at chosen instants: the walk reads nothing else of a job.
    private async Task<long> SeedFinishedAsync(
        int ns,
        int definition,
        DateTime createdAtUtc,
        DateTime retentionUntilUtc,
        CancellationToken ct
    )
    {
        var jobId = await Db.From<JobEntity>()
            .InsertAsync<long>(
                new JobEntity
                {
                    JobRef = Guid.NewGuid(),
                    NamespaceId = ns,
                    DefinitionId = definition,
                    InputFormatId = 1,
                    Input = [0],
                    EventRecording = EventRecordingCode.Failures,
                },
                ct
            );
        // An insert takes the database's own clock for the creation instant, so the chosen one is written after it.
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET created_at_utc = @p_created WHERE job_id = @p_id",
            ct,
            ("@p_created", createdAtUtc),
            ("@p_id", jobId)
        );
        await Db.From<JobStateEntity>()
            .InsertAsync<long>(
                new JobStateEntity
                {
                    Id = jobId,
                    NamespaceId = ns,
                    Status = JobStatusCode.Succeeded,
                    Priority = JobPriorityCode.Normal,
                    ExecutionNumber = 1,
                    RetentionUntilUtc = retentionUntilUtc,
                },
                ct
            );
        return jobId;
    }
}
