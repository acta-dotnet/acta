using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobId</c>: a ref or a root deduplication key resolves to the job's internal id, an id lookup
/// answers its own id, and a lookup that matches no job answers <c>null</c>.
/// </summary>
[ConformanceSpec(
    "get-job-id.resolve",
    "GetJobId resolves a ref or deduplication key to the id, null for no match",
    Area = "Reads",
    Contract = "GetJobId returns the internal id of the job a ref or a root deduplication key names, returns an id lookup's own id, and returns null when no job matches."
)]
public sealed class GetJobIdSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A ref and a deduplication key resolve to the enqueued job's id")]
    public async Task Ref_and_deduplication_key_resolve_to_the_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var deduplicationKey = TestKey("get-job-id");
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(1, 2), new JobEnqueueOptions { DeduplicationKey = deduplicationKey }, ct);
        var handler = Services.GetRequiredService<GetJobIdHandler>();

        var byRef = await handler.HandleAsync(new GetJobIdQuery(JobLookup.ByRef(enqueued.JobRef)), ct);
        var byKey = await handler.HandleAsync(new GetJobIdQuery(JobLookup.ByDeduplicationKey(TestNamespace, deduplicationKey)), ct);

        Assert.Equal(enqueued.JobId, byRef?.JobId);
        Assert.Equal(enqueued.JobId, byKey?.JobId);
    }

    [Fact(DisplayName = "An id lookup answers its own id, and a ref or key that matches no job answers null")]
    public async Task Id_passes_through_and_a_miss_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = Services.GetRequiredService<GetJobIdHandler>();

        Assert.Equal(long.MaxValue, (await handler.HandleAsync(new GetJobIdQuery(JobLookup.ById(long.MaxValue)), ct))?.JobId);
        Assert.Null(await handler.HandleAsync(new GetJobIdQuery(JobLookup.ByRef(JobRef.New())), ct));
        Assert.Null(await handler.HandleAsync(new GetJobIdQuery(JobLookup.ByDeduplicationKey(TestNamespace, "no-such-key")), ct));
    }
}
