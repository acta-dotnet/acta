using Acta.Modules.Execution.Jobs;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Set-based batch enqueue spec - exercises 1..5000-row enqueues through the same
/// <see cref="EnqueueBatch.Run"/> call that the public
/// <c>IJobs.EnqueueAsync</c> wraps. Asserts:
///   * batch=1 with tags lands one <c>job</c> row and the expected <c>tags</c> rows
///     (no <c>events</c> on enqueue - the Job row's own creation columns are the
///     enqueue record);
///   * batch=1000 lands 1000 jobs in one round-trip, with per-ordinal outcomes aligned
///     to the input list;
///   * a shorter payload following a longer payload is persisted byte-for-byte (SQL Server streams
///     one reused TVP record, so replacing the whole binary field is required to truncate it).
/// Identical assertions run against SqlServer and Postgres via the provider one-liners under
/// <c>Acta.Tests.Conformance.SqlServer</c> / <c>Acta.Tests.Conformance.Postgres</c>.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.batch-insert",
    "Batch enqueue lands one job row per input ordinal with no enqueue event",
    Area = "Enqueue",
    Contract = "A batch enqueue inserts one Ready job per input row with positionally-aligned outcomes, persists tags, and writes no events on enqueue.",
    Arrange = "An add-numbers definition is registered in the test namespace.",
    Act = "A one-row batch with tags is enqueued, followed by a 1000-row batch.",
    Assert = "Each input row lands one Ready job with positionally-aligned outcomes and byte-exact input, its tags are persisted, and no events are written on enqueue."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class EnqueueSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Enqueue-only spec - we never call StartAsync, so no claim/execute loop runs. But we still
    // run InitializeAsync (worker-mode) so the per-test namespace is upserted and registered in
    // Runtime.RegisteredNamespaceIds; the inserted workers row is harmless overhead.
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "A single enqueue lands one Ready job with its tags and writes no events")]
    public async Task Enqueue_one_job_lands_one_row_with_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(2, 3));

        var deduplicationKey = TestKey("single");
        JobEnqueueRow[] command =
        [
            new JobEnqueueRow(
                NamespaceName: TestNamespace,
                JobName: "add-numbers",
                Input: payload,
                DeduplicationKey: deduplicationKey,
                Tags: [new TagInput("env", "EU-West"), new TagInput("feature", "batch-enqueue")]
            ),
        ];

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, command, ct);

        Assert.Single(result);
        var outcome = result[0];
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
        Assert.True(outcome.JobId > 0);

        var jobRow = await ReadJobAsync(outcome.JobId, ct);
        Assert.Equal(outcome.JobId, jobRow.Id);
        Assert.Equal(JobStatusCode.Ready, jobRow.Status);
        Assert.Equal(namespaceId, jobRow.NamespaceId);
        Assert.Equal(deduplicationKey, jobRow.DeduplicationKey);
        Assert.Null(jobRow.LineageRootId);

        // Enqueue does not write events - the Job row's own creation columns are the
        // enqueue record. events rows appear only for state transitions (claim/start/finish/...).
        var enqueueEventCount = await Db.From<JobEvent>().Where(e => e.JobId == outcome.JobId).CountAsync(ct);
        Assert.Equal(0, enqueueEventCount);

        var tagRows = await Db.From<Tag>().Where(t => t.ScopeCode == TagScopeCode.Job && t.ScopeId == outcome.JobId).ToListAsync(ct);
        Assert.Equal(2, tagRows.Count);
        Assert.Contains(tagRows, t => t.Name == "env" && t.Value == "EU-West" && t.ValueSearch == "EU-WEST");
        Assert.Contains(tagRows, t => t.Name == "feature" && t.Value == "batch-enqueue");
    }

    [Fact(DisplayName = "A 1000-row batch lands 1000 Ready jobs with positionally-aligned outcomes and unique JobIds")]
    public async Task Enqueue_one_thousand_jobs_lands_one_thousand_rows()
    {
        const int batchSize = 1000;
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var serializer = serializers.Resolve(JobPayloadFormat.Json.Id);

        var rows = new JobEnqueueRow[batchSize];
        for (var i = 0; i < batchSize; i++)
        {
            var payload = serializer.Serialize(new AddNumbers(i, i + 1));
            rows[i] = new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload);
        }

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);

        Assert.Equal(batchSize, result.Count);
        var seenJobIds = new HashSet<long>();
        for (var i = 0; i < batchSize; i++)
        {
            // Outcomes are positionally aligned with the input list - caller can zip without sorting.
            // DB-assigned JobIds are unique per row.
            Assert.True(result[i].JobId > 0);
            Assert.True(seenJobIds.Add(result[i].JobId));
            Assert.Equal(JobEnqueueAction.Inserted, result[i].Action);
        }

        // Count only the enqueued jobs (null deduplication_key). The shared test manifest declares a
        // recurring definition, so InitializeAsync also created its recurring slot row
        // with a non-null deduplication key in this namespace - exclude it.
        var jobCount = await Db.From<Job>().Where(j => j.NamespaceId == namespaceId && j.DeduplicationKey == null).CountAsync(ct);
        Assert.Equal(batchSize, jobCount);

        // No events rows on enqueue (see class-level summary). Filter to job-scoped events
        // (job_id IS NOT NULL) so the worker.registered / job.definition.registered events that
        // InitializeAsync emits at namespace scope don't pollute the count.
        var eventCount = await Db.From<JobEvent>().Where(e => e.NamespaceId == namespaceId && e.JobId != null).CountAsync(ct);
        Assert.Equal(0, eventCount);
    }

    [Fact(DisplayName = "A shorter payload after a longer payload is persisted without retaining trailing bytes")]
    public async Task Enqueue_batch_replaces_the_whole_payload_for_each_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var longBytes = "{\"left\":123456789,\"right\":987654321}"u8.ToArray();
        var shortBytes = "{\"left\":1,\"right\":2}"u8.ToArray();
        JobEnqueueRow[] rows =
        [
            new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, longBytes)),
            new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, shortBytes)),
        ];

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var outcomes = await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);

        Assert.Equal(2, outcomes.Count);
        var first = await Db.From<Job>().Where(j => j.Id == outcomes[0].JobId).SingleOrDefaultAsync(ct);
        var second = await Db.From<Job>().Where(j => j.Id == outcomes[1].JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(longBytes, first.Input);
        Assert.Equal(shortBytes, second.Input);
    }
}
