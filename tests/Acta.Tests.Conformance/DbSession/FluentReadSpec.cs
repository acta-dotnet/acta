using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.DbSession;

/// <summary>
/// End-to-end coverage of Acta's reflection-based fluent table reader. Enqueues
/// one job, then reads it back through <c>session.From&lt;Job&gt;()</c> and
/// <c>session.From&lt;JobRuntime, JobSummary&gt;()</c> against both provider backends. Exercises whole-entity
/// materialization, projection (column pruning), and the <c>Count</c> terminal.
/// </summary>
[ConformanceSpec(
    "fluent-read.testing-seam",
    "The fluent reader materializes projects and counts entities",
    Area = "Testing",
    Contract = "The fluent reader materializes whole entities, prunes columns on projection, supports compound and IN predicates, and counts matching rows.",
    Arrange = "One job is enqueued in the test namespace on a live provider schema.",
    Act = "The job is read back through From<Job>() and the From<JobRuntime, JobSummary>() projection with compound and IN predicates and Count.",
    Assert = "Whole entities materialize, projections prune columns, predicates filter to the matching rows, and Count returns the matching total."
)]
public abstract class FluentReadSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Whole-entity materialization returns the matching row")]
    public async Task Where_equals_finds_enqueued_job_by_deduplication_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = await EnqueueOne(ct);

        var jobs = await Db.From<Job>().Where(j => j.DeduplicationKey == deduplicationKey).ToListAsync(ct);

        var job = Assert.Single(jobs);
        Assert.Equal(deduplicationKey, job.DeduplicationKey);
        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == job.Id).SingleOrDefaultAsync(ct);
        Assert.Equal(JobStatusCode.Ready, runtime!.Status);
    }

    [Fact(DisplayName = "A compound predicate filters to the matching row")]
    public async Task Where_compound_predicate_filters_correctly()
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var deduplicationKey = await EnqueueOne(ct);

        var jobs = await Db.From<Job>()
            .Where(j => j.NamespaceId == namespaceId && j.ParentId == null && j.DeduplicationKey == deduplicationKey)
            .ToListAsync(ct);

        var job = Assert.Single(jobs);
        var runtimes = await Db.From<JobRuntime>()
            .Where(r => r.Id == job.Id && r.Status == JobStatusCode.Ready && r.ExecutionNumber == 0)
            .ToListAsync(ct);
        Assert.Single(runtimes);
    }

    [Fact(DisplayName = "Projection prunes to the declared columns")]
    public async Task Projection_reads_only_declared_columns()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = await EnqueueOne(ct);

        var job = await Db.From<Job>().Where(j => j.DeduplicationKey == deduplicationKey).SingleOrDefaultAsync(ct);
        var rows = await Db.From<JobRuntime, JobSummary>().Where(r => r.Id == job!.Id).ToListAsync(ct);

        var row = Assert.Single(rows);
        Assert.True(row.Id > 0);
        Assert.Equal(JobStatusCode.Ready, row.Status);
    }

    [Fact(DisplayName = "Count returns the matching row count")]
    public async Task Count_returns_matching_row_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = await EnqueueOne(ct);

        var count = await Db.From<Job>().Where(j => j.DeduplicationKey == deduplicationKey).CountAsync(ct);

        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "An IN predicate filters to the matching status set")]
    public async Task In_predicate_matches_status_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = await EnqueueOne(ct);

        var job = await Db.From<Job>().Where(j => j.DeduplicationKey == deduplicationKey).SingleOrDefaultAsync(ct);
        var inflight = new List<JobStatusCode> { JobStatusCode.Ready, JobStatusCode.Dispatched, JobStatusCode.Executing };
        var rows = await Db.From<JobRuntime>().Where(r => r.Id == job!.Id && inflight.Contains(r.Status)).ToListAsync(ct);

        Assert.Single(rows);
    }

    private async Task<string> EnqueueOne(CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(2, 3));

        var deduplicationKey = TestKey("fluent-read");
        _ = Services.GetRequiredService<ISqlDialect>();
        await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload, DeduplicationKey: deduplicationKey)],
            ct
        );
        return deduplicationKey;
    }
}
