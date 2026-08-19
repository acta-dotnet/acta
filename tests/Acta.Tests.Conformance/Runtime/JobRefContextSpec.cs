using System.Text;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves the handler sees the same public JobRef that the enqueue outcome carries.
/// </summary>
[ConformanceSpec(
    "job-context.job-ref",
    "Handler sees its public JobRef matching the enqueue outcome",
    Area = "Execution",
    Contract = "A handler receives the same public JobRef via ctx.JobRef that the caller gets from JobEnqueueOutcome.",
    Arrange = "A jobref-probe handler that stores ctx.JobRef into a variable is registered.",
    Act = "The job is enqueued and runs once.",
    Assert = "The JobRef the handler stored matches the JobRef the enqueue outcome returned."
)]
public abstract class JobRefContextSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Handler reads the same JobRef the enqueue outcome returned, stable across claim and execution")]
    public async Task Handler_sees_its_public_JobRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "jobref-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var seen = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<IExecutionStore>(),
            enqueued.JobId,
            JobCheckpointKindCode.Variable,
            "seen-ref",
            ct
        );
        Assert.NotNull(seen);
        Assert.Contains(enqueued.JobRef.ToString(), Encoding.UTF8.GetString(seen!.Value));
    }
}
