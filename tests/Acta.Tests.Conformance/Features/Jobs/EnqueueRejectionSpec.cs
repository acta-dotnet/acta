using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Batch-enqueue rejection spec - exercises the C# guards that run before any SQL: same-batch
/// duplicate DeduplicationKeys (case-insensitive), duplicate tag names per row, and unknown namespace/job.
/// Each rejection is atomic: the whole batch throws and no job rows land. Distinct and null
/// DeduplicationKeys are unaffected. Identical assertions run against SqlServer and Postgres.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.batch-rejection",
    "Same-batch duplicate deduplication keys or malformed rows reject the batch",
    Area = "Enqueue",
    Contract = "A batch with a same-batch duplicate DeduplicationKey, duplicate tag names, or an unknown namespace or job is rejected atomically and no job rows are inserted.",
    Arrange = "A namespace with an add-numbers definition is registered so only the pre-SQL enqueue guards are in play.",
    Act = "Batches with duplicate DeduplicationKeys, duplicate tag names, or an unknown namespace or job are enqueued, plus one batch of distinct and null keys.",
    Assert = "Each violating batch is rejected atomically persisting nothing, while the batch of distinct and null keys inserts."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class EnqueueRejectionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Enqueue-only spec - InitializeAsync (worker mode) registers the namespace so EnqueueBatch.Run
    // can resolve it; we never start the claim/execute loop.
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "A same-batch duplicate DeduplicationKey throws DuplicateDeduplicationKeyInBatchException and persists nothing")]
    public async Task Same_batch_duplicate_deduplication_key_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command = [Row("dup-1", new AddNumbers(1, 1)), Row("dup-1", new AddNumbers(2, 2))];

        var ex = await Assert.ThrowsAsync<DuplicateDeduplicationKeyInBatchException>(() => RunAsync(command, ct));
        Assert.Equal("dup-1", ex.DeduplicationKey);
        Assert.Equal(TestNamespace, ex.RootJobNamespace);
        Assert.Null(ex.ParentJobId);
        Assert.Equal(0, ex.FirstOrdinal);
        Assert.Equal(1, ex.SecondOrdinal);

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Duplicate DeduplicationKeys differing only by case are rejected (case-insensitive)")]
    public async Task Same_batch_duplicate_deduplication_key_differing_only_by_case_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command = [Row("Invoice-7", new AddNumbers(1, 1)), Row("invoice-7", new AddNumbers(2, 2))];

        await Assert.ThrowsAsync<DuplicateDeduplicationKeyInBatchException>(() => RunAsync(command, ct));
        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Duplicate DeduplicationKeys with different payloads are still rejected")]
    public async Task Same_batch_duplicate_deduplication_key_with_different_payloads_still_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command = [Row("dup-2", new AddNumbers(10, 20)), Row("dup-2", new AddNumbers(99, 99))];

        await Assert.ThrowsAsync<DuplicateDeduplicationKeyInBatchException>(() => RunAsync(command, ct));
        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Distinct and null DeduplicationKeys coexist in one batch and all insert")]
    public async Task Distinct_and_null_deduplication_keys_coexist()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command =
        [
            Row("invoice-a", new AddNumbers(1, 1)),
            Row("invoice-b", new AddNumbers(2, 2)),
            Row(null, new AddNumbers(3, 3)),
            Row(null, new AddNumbers(4, 4)),
        ];

        var result = await RunAsync(command, ct);
        Assert.Equal(4, result.Count);
        Assert.All(result, r => Assert.Equal(JobEnqueueAction.Inserted, r.Action));
        Assert.Equal(before + 4, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Duplicate tag names on a row throw ArgumentException and persist nothing")]
    public async Task Same_batch_duplicate_tag_names_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command = [Row("tagged-1", new AddNumbers(1, 1), [new TagInput("env", "a"), new TagInput("env", "b")])];

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(command, ct));
        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Rejection is atomic so a valid row in a rejected batch never lands")]
    public async Task Mixed_valid_and_invalid_batch_persists_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        // One well-formed row plus a same-batch duplicate pair: the whole batch is rejected before
        // SQL, so the valid row never lands either.
        JobEnqueueRow[] command =
        [
            Row("good-row", new AddNumbers(1, 1)),
            Row("dup-3", new AddNumbers(2, 2)),
            Row("dup-3", new AddNumbers(3, 3)),
        ];

        await Assert.ThrowsAsync<DuplicateDeduplicationKeyInBatchException>(() => RunAsync(command, ct));
        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "An unknown namespace or job throws and persists nothing")]
    public async Task Unknown_namespace_or_job_throws_and_persists_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await CountJobsAsync(ct);

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Unknown job: the routine's resolved-count check throws (provider-native, not wrapped).
        JobEnqueueRow[] command = [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "no-such-job", Input: JobPayload.None)];

        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueBatchAsync(Services, command, ct));
        Assert.Equal(before, await CountJobsAsync(ct));
    }

    private JobEnqueueRow Row(string? deduplicationKey, AddNumbers input, IReadOnlyList<TagInput>? tags = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(input);
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            DeduplicationKey: deduplicationKey,
            Tags: tags
        );
    }

    private async Task<IReadOnlyList<JobEnqueueOutcome>> RunAsync(IReadOnlyList<JobEnqueueRow> command, CancellationToken ct)
    {
        _ = Services.GetRequiredService<ISqlDialect>();
        return await EnqueueTestOps.EnqueueBatchAsync(Services, command, ct);
    }

    private async Task<int> CountJobsAsync(CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        return await Db.From<Job>().Where(j => j.NamespaceId == namespaceId).CountAsync(ct);
    }
}
