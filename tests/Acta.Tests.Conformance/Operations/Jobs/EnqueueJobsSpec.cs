using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Set-based batch enqueue spec - exercises 1..1000-row enqueues through the same
/// <see cref="EnqueueJobs.Run"/> call that the public
/// <c>IJobs.EnqueueAsync</c> wraps. Asserts:
///   * batch=1 with tags lands one <c>job</c> row and the expected <c>tags</c> rows
///     (no <c>events</c> on enqueue - the Job row's own creation columns are the
///     enqueue record);
///   * batch=1000 lands 1000 jobs in one round-trip, with per-ordinal outcomes aligned
///     to the input list;
///   * a shorter payload following a longer payload is persisted byte-for-byte (SQL Server streams
///     one reused TVP record, so replacing the whole binary field is required to truncate it);
///   * a payload with a format and no bytes is stored zero-length, since the input pair check refuses a format
///     with NULL bytes.
/// Identical assertions run against SqlServer and Postgres via the provider one-liners under
/// <c>Acta.Tests.Conformance.SqlServer</c> / <c>Acta.Tests.Conformance.Postgres</c>.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.batch-insert",
    "Batch enqueue lands one job row per input ordinal with no enqueue event",
    Area = "Enqueue",
    Contract = "A batch enqueue inserts one Ready job per input row with positionally-aligned outcomes, persists tags, and writes no events on enqueue."
)]
public sealed class EnqueueJobsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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

        var deduplicationKey = Unique("single");
        JobEnqueueValues[] command =
        [
            new JobEnqueueValues(
                NamespaceName: TestNamespace,
                JobName: "add-numbers",
                Input: payload,
                DeduplicationKey: deduplicationKey,
                Tags: [new TagInput("env", "EU-West"), new TagInput("feature", "batch-enqueue")]
            ),
        ];

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var result = (await EnqueueTestOps.EnqueueBatchAsync(Services, command, ct)).Jobs;

        Assert.Single(result);
        var outcome = result[0];
        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);
        Assert.True(outcome.JobId > 0);

        var jobRow = await ReadJobAsync(outcome.JobId, ct);
        Assert.Equal(outcome.JobId, jobRow.Id);
        Assert.Equal(JobStatusCode.Ready, jobRow.Status);
        Assert.Equal(namespaceId, jobRow.NamespaceId);
        Assert.Equal(deduplicationKey, jobRow.DeduplicationKey);
        Assert.Null(jobRow.LineageRootJobId);

        // Enqueue does not write events - the Job row's own creation columns are the
        // enqueue record. events rows appear only for state transitions (claim/start/finish/...).
        var enqueueEventCount = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == outcome.JobId)
            .CountAsync(ct);
        Assert.Equal(0, enqueueEventCount);

        var tagRows = await Db.From<TagEntity>().Where(t => t.EntityCode == EntityCode.Job && t.EntityId == outcome.JobId).ToListAsync(ct);
        Assert.Equal(2, tagRows.Count);
        Assert.Contains(tagRows, t => t.TagName == "env" && t.TagValue == "EU-West" && t.TagValueSearch == "EU-WEST");
        Assert.Contains(tagRows, t => t.TagName == "feature" && t.TagValue == "batch-enqueue");
    }

    [Fact(DisplayName = "A 1000-row batch lands 1000 Ready jobs with positionally-aligned outcomes and unique JobIds")]
    public async Task Enqueue_one_thousand_jobs_lands_one_thousand_rows()
    {
        const int batchSize = 1000;
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var serializer = serializers.Resolve(JobPayloadFormat.Json.Id);

        var rows = new JobEnqueueValues[batchSize];
        for (var i = 0; i < batchSize; i++)
        {
            var payload = serializer.Serialize(new AddNumbers(i, i + 1));
            rows[i] = new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload);
        }

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var result = (await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct)).Jobs;

        Assert.Equal(batchSize, result.Count);
        var seenJobIds = new HashSet<long>();
        for (var i = 0; i < batchSize; i++)
        {
            // Outcomes are positionally aligned with the input list - caller can zip without sorting.
            // DB-assigned JobIds are unique per row.
            Assert.True(result[i].JobId > 0);
            Assert.True(seenJobIds.Add(result[i].JobId));
            Assert.Equal(EnqueueJobAction.Inserted, result[i].Action);
        }

        // Count only the enqueued jobs: the shared test manifest declares scheduled definitions, whose permanent
        // slots InitializeAsync also created in this namespace.
        var jobCount = await Db.From<JobEntity>().Where(j => j.NamespaceId == namespaceId && j.Kind == JobKindCode.Ordinary).CountAsync(ct);
        Assert.Equal(batchSize, jobCount);

        // No events rows on enqueue (see class-level summary). Filter to job-subject events so the
        // worker and definition events that InitializeAsync emits don't pollute the count.
        var eventCount = await Db.From<EventEntity>()
            .Where(e => e.NamespaceId == namespaceId && e.EntityCode == EntityCode.Job)
            .CountAsync(ct);
        Assert.Equal(0, eventCount);
    }

    [Fact(DisplayName = "A shorter payload after a longer payload is persisted without retaining trailing bytes")]
    public async Task Enqueue_batch_replaces_the_whole_payload_for_each_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var longBytes = "{\"left\":123456789,\"right\":987654321}"u8.ToArray();
        var shortBytes = "{\"left\":1,\"right\":2}"u8.ToArray();
        JobEnqueueValues[] rows =
        [
            new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, longBytes)),
            new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, shortBytes)),
        ];

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var outcomes = (await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct)).Jobs;

        Assert.Equal(2, outcomes.Count);
        var first = await Db.From<JobEntity>().Where(j => j.Id == outcomes[0].JobId).SingleOrDefaultAsync(ct);
        var second = await Db.From<JobEntity>().Where(j => j.Id == outcomes[1].JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(longBytes, first.Input);
        Assert.Equal(shortBytes, second.Input);
    }

    [Fact(DisplayName = "A payload with a format and no bytes lands as a zero-length input, not NULL, alone and in a batch")]
    public async Task Enqueue_stores_an_empty_payload_with_a_format_as_a_zero_length_input()
    {
        var ct = TestContext.Current.CancellationToken;
        JobPayload[] payloads = [JobPayload.Bytes([]), JobPayload.FromBytes(JobPayloadFormat.Custom(200, "empty-custom"), [])];
        JobEnqueueValues[] rows = [.. payloads.Select(payload => new JobEnqueueValues(TestNamespace, "add-numbers", payload))];

        // One row goes through enqueue_job and a longer batch through enqueue_jobs.
        List<EnqueueJobResponse> outcomes = [];
        foreach (var row in rows)
        {
            outcomes.Add(await EnqueueTestOps.EnqueueJobAsync(Services, row, ct));
        }
        outcomes.AddRange((await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct)).Jobs);

        for (var i = 0; i < outcomes.Count; i++)
        {
            var jobId = outcomes[i].JobId;
            var job = await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
            Assert.NotNull(job);
            Assert.Equal(payloads[i % payloads.Length].Format.Id, job.InputFormatId);
            Assert.NotNull(job.Input);
            Assert.Empty(job.Input);
        }
    }
}
