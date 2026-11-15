using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>ListChildJobsByName</c>, the read a map's replay answers the children it started from.</summary>
[ConformanceSpec(
    "list-child-jobs-by-name.found",
    "A parent's children are found by the names it started them under",
    Area = "Execution",
    Contract = "The read answers each of the parent's direct children whose name was asked for, and no child of another parent or of another name."
)]
[Deploys("job-child-echo")]
public sealed class ListChildJobsByNameSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ListChildJobsByNameSpec>>
{
    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "Each child named is answered with its id and ref, a finished one included")]
    public async Task Each_named_child_is_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await EnqueueAsync(parentJobId: null, Unique("parent"), ct);
        var a = await EnqueueAsync(parent.JobId, Unique("a"), ct);
        var b = await EnqueueAsync(parent.JobId, Unique("b"), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(b, ct));

        var found = (await Execution.ListChildJobsByNameAsync(parent.JobId, [Unique("a"), Unique("b")], ct)).Children;

        Assert.Equal(
            [(a.JobId, a.JobRef, Unique("a")), (b.JobId, b.JobRef, Unique("b"))],
            found.OrderBy(child => child.JobId).Select(child => (child.JobId, new JobRef(child.JobRef), child.DeduplicationKey))
        );
    }

    [Fact(DisplayName = "A name no child holds, another parent's child, a grandchild, and a child not named are never answered")]
    public async Task Only_the_named_children_of_the_parent_are_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await EnqueueAsync(parentJobId: null, Unique("parent"), ct);
        var other = await EnqueueAsync(parentJobId: null, Unique("other"), ct);
        var child = await EnqueueAsync(parent.JobId, Unique("child"), ct);
        await EnqueueAsync(parent.JobId, Unique("unnamed"), ct);
        await EnqueueAsync(other.JobId, Unique("cousin"), ct);
        await EnqueueAsync(child.JobId, Unique("grandchild"), ct);

        var found = (
            await Execution.ListChildJobsByNameAsync(
                parent.JobId,
                [Unique("child"), Unique("cousin"), Unique("grandchild"), Unique("missing")],
                ct
            )
        ).Children;

        Assert.Equal(child.JobId, Assert.Single(found).JobId);
    }

    private async Task<EnqueueJobResponse> EnqueueAsync(long? parentJobId, string key, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "job-child-echo",
                JobPayload.Json(new ChildEcho(1)),
                DeduplicationKey: key,
                ParentJobId: parentJobId
            ),
            ct
        );
}
