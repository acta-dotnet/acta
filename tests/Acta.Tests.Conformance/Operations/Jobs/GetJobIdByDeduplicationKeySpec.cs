using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobIdByDeduplicationKey</c> - maps a <c>(namespace, deduplication_key)</c> pair to
/// the framework-assigned <c>job.id</c>. A known key resolves to the enqueued job's id; an unknown
/// key returns <c>null</c>.
/// </summary>
[ConformanceSpec(
    "get-job-id-by-deduplication-key.lookup",
    "GetJobIdByDeduplicationKey returns the id for a known key, null otherwise",
    Area = "Enqueue",
    Contract = "GetJobIdByDeduplicationKey resolves a root job's id from its namespace and deduplication key, and returns null when no row matches."
)]
public sealed class GetJobIdByDeduplicationKeySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Known deduplication key resolves to the enqueued job id and an unknown key returns null")]
    public async Task Known_deduplication_key_resolves_to_job_id_and_unknown_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = Unique("ck-resolve");

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.DeduplicationKey(deduplicationKey), ct);
        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);

        var resolved = await Services
            .GetRequiredService<GetJobIdByDeduplicationKeyHandler>()
            .HandleAsync(new GetJobIdByDeduplicationKeyQuery(TestNamespace, deduplicationKey), ct);
        Assert.Equal(outcome.JobId, resolved?.JobId);

        var missing = await Services
            .GetRequiredService<GetJobIdByDeduplicationKeyHandler>()
            .HandleAsync(new GetJobIdByDeduplicationKeyQuery(TestNamespace, "no-such-key"), ct);
        Assert.Null(missing);
    }
}
