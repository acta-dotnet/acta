using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>ResolveJobIdByRef</c> - maps the public <c>job_ref</c> to the internal
/// <c>job.id</c>. Enqueue assigns the ref server-side, the ref round-trips through the read seam,
/// dedup returns the existing row's ref, and an unknown ref returns <c>null</c>.
/// </summary>
[ConformanceSpec(
    "resolve-job-id-by-ref.lookup",
    "Enqueue assigns a job ref that resolves to the job; unknown refs return null",
    Area = "Enqueue",
    Contract = "Every enqueued job carries a server-generated job_ref that resolves to its internal id, and an unknown ref resolves to null."
)]
public abstract class ResolveJobIdByRefSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "Enqueue returns a non-empty ref that resolves and reads back the same job, dedup echoes the existing ref, and an unknown ref returns null"
    )]
    public async Task Job_ref_round_trips_and_unknown_ref_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = TestKey("ck-resolve-ref");

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(1, 2), new JobEnqueueOptions { DeduplicationKey = deduplicationKey }, ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
        Assert.NotEqual(Guid.Empty, outcome.JobRef.Value);

        var resolved = await Services.GetRequiredService<IJobStore>().ResolveJobIdByRefAsync(outcome.JobRef.Value, ct);
        Assert.Equal(outcome.JobId, resolved);

        var snapshot = await Jobs.GetAsync(JobLookup.ByRef(outcome.JobRef), ct);
        Assert.NotNull(snapshot);
        Assert.Equal(outcome.JobId, snapshot.JobId);
        Assert.Equal(outcome.JobRef, snapshot.JobRef);

        var deduplicated = await Jobs.EnqueueAsync(new AddNumbers(1, 2), new JobEnqueueOptions { DeduplicationKey = deduplicationKey }, ct);
        Assert.Equal(JobEnqueueAction.Deduplicated, deduplicated.Action);
        Assert.Equal(outcome.JobRef, deduplicated.JobRef);

        var missing = await Services.GetRequiredService<IJobStore>().ResolveJobIdByRefAsync(Guid.NewGuid(), ct);
        Assert.Null(missing);
    }
}
