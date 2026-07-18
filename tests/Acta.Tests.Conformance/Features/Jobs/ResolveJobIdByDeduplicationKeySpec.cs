using Acta.Features.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>ResolveJobIdByDeduplicationKey</c> - maps a <c>(namespace, deduplication_key)</c> pair to
/// the framework-assigned <c>job.id</c>. A known key resolves to the enqueued job's id; an unknown
/// key returns <c>null</c>.
/// </summary>
[ConformanceSpec(
    "resolve-job-id-by-deduplication-key.lookup",
    "ResolveJobIdByDeduplicationKey returns the id for a known key, null otherwise",
    Area = "Enqueue",
    Contract = "ResolveJobIdByDeduplicationKey resolves a root job's id from its namespace and deduplication key, and returns null when no row matches.",
    Arrange = "A job is enqueued with a known deduplication key.",
    Act = "The id is resolved by that key and by an unknown key.",
    Assert = "The known key resolves to the enqueued job's id and the unknown key returns null."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResolveJobIdByDeduplicationKeyAsync))]
public abstract class ResolveJobIdByDeduplicationKeySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Known deduplication key resolves to the enqueued job id and an unknown key returns null")]
    public async Task Known_deduplication_key_resolves_to_job_id_and_unknown_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = TestKey("ck-resolve");

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(1, 2), new JobEnqueueOptions { DeduplicationKey = deduplicationKey }, ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

        var resolved = await Services
            .GetRequiredService<IJobStore>()
            .ResolveJobIdByDeduplicationKeyAsync(TestNamespace, deduplicationKey, ct);
        Assert.Equal(outcome.JobId, resolved);

        var missing = await Services.GetRequiredService<IJobStore>().ResolveJobIdByDeduplicationKeyAsync(TestNamespace, "no-such-key", ct);
        Assert.Null(missing);
    }
}
