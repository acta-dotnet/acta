using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Execution;

/// <summary>
/// Proves the handler sees the same public JobRef that the enqueue outcome carries.
/// </summary>
[ConformanceSpec(
    "job-context.job-ref",
    "Handler sees its public JobRef matching the enqueue outcome",
    Area = "Execution",
    Contract = "A handler receives the same public JobRef via ctx.JobRef that the caller gets from EnqueueJobResponse."
)]
public sealed class JobRefContextSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Handler reads the same JobRef the enqueue outcome returned, stable across claim and execution")]
    public async Task Handler_sees_its_public_JobRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "jobref-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var seen = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<Acta.Execution>(),
            enqueued.JobId,
            CheckpointKindCode.Variable,
            "seen-ref",
            ct
        );
        Assert.NotNull(seen);
        Assert.Contains(enqueued.JobRef.ToString(), Encoding.UTF8.GetString(seen!.Value));
    }
}
