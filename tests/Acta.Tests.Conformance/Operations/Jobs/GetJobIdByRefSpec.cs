using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobIdByRef</c> - maps the public <c>job_ref</c> to the internal
/// <c>job.id</c>. Enqueue assigns the ref server-side, the ref round-trips through the read seam,
/// dedup returns the existing row's ref, and an unknown ref returns <c>null</c>.
/// </summary>
[ConformanceSpec(
    "get-job-id-by-ref.lookup",
    "Enqueue assigns a job ref that resolves to the job; unknown refs return null",
    Area = "Enqueue",
    Contract = "Every enqueued job carries a server-generated job_ref that resolves to its internal id, and an unknown ref resolves to null."
)]
[Deploys("add-numbers")]
public sealed class GetJobIdByRefSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobIdByRefSpec>>
{
    [Fact(
        DisplayName = "Enqueue returns a non-empty ref that resolves and reads back the same job, dedup echoes the existing ref, and an unknown ref returns null"
    )]
    public async Task Job_ref_round_trips_and_unknown_ref_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = Unique("ck-resolve-ref");

        var outcome = await Jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.DeduplicationKey(deduplicationKey), ct);
        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);
        Assert.NotEqual(Guid.Empty, outcome.JobRef.Value);

        var resolved = await Services
            .GetRequiredService<GetJobIdByRefHandler>()
            .HandleAsync(new GetJobIdByRefQuery(outcome.JobRef.Value), ct);
        Assert.Equal(outcome.JobId, resolved?.JobId);

        var snapshot = await Jobs.GetAsync(JobLookup.ByRef(outcome.JobRef), ct);
        Assert.NotNull(snapshot);
        Assert.Equal(outcome.JobId, snapshot.JobId);
        Assert.Equal(outcome.JobRef, snapshot.JobRef);

        var deduplicated = await Jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.DeduplicationKey(deduplicationKey), ct);
        Assert.Equal(EnqueueJobAction.Deduplicated, deduplicated.Action);
        Assert.Equal(outcome.JobRef, deduplicated.JobRef);

        var missing = await Services.GetRequiredService<GetJobIdByRefHandler>().HandleAsync(new GetJobIdByRefQuery(Guid.NewGuid()), ct);
        Assert.Null(missing);
    }
}
