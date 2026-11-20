using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>ListJobsByRef</c>, the read the outbox relay takes after a failed enqueue: each ref a job holds
/// answers that job's namespace and name, and a ref no job holds is absent.
/// </summary>
[ConformanceSpec(
    "list-jobs-by-ref.lookup",
    "Each held ref answers its job's namespace and name, and an unheld ref is absent",
    Area = "Enqueue",
    Contract = "ListJobsByRef answers one item per given ref a job holds, with that job's namespace and definition name, and none for a ref no job holds."
)]
[Deploys("add-numbers")]
public sealed class ListJobsByRefSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ListJobsByRefSpec>>
{
    [Fact(DisplayName = "Held refs answer their job's namespace and name, and an unheld ref is absent")]
    public async Task Held_refs_answer_their_jobs_and_an_unheld_ref_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Jobs.EnqueueAsync(new AddNumbers(1, 2), ct: ct);
        var second = await Jobs.EnqueueAsync(new AddNumbers(3, 4), ct: ct);

        var found = (
            await Services
                .GetRequiredService<JobsOperations>()
                .ListJobsByRefAsync([first.JobRef.Value, Guid.NewGuid(), second.JobRef.Value], ct)
        ).Jobs;

        Assert.Equal(2, found.Count);
        Assert.Equal(new HashSet<Guid> { first.JobRef.Value, second.JobRef.Value }, found.Select(static job => job.JobRef).ToHashSet());
        Assert.All(found, job => Assert.Equal((TestNamespace, "add-numbers"), (job.JobNamespace, job.JobName)));
    }
}
