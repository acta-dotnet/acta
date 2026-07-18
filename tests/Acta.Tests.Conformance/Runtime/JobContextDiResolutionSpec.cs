using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves <see cref="JobContext"/> is resolvable from the per-attempt DI scope: the
/// <c>context-probe</c> handler is an instance type whose <see cref="JobContext"/> arrives by
/// constructor injection (created via <c>ActivatorUtilities.CreateInstance</c> from the attempt
/// scope - the same path a MediatR <c>IRequestHandler</c> takes). The handler echoes the injected
/// context's identity, so the persisted result confirms DI handed it the running job's context.
/// Identical assertions run against SqlServer and Postgres via the provider one-liners.
/// </summary>
[ConformanceSpec(
    "job-context.di-resolution",
    "JobContext is resolvable by constructor injection in the attempt scope",
    Area = "Execution",
    Contract = "An instance handler receives a populated JobContext by constructor injection from the per-attempt DI scope matching the running job's identity.",
    Arrange = "A context-probe instance handler taking JobContext by constructor injection is registered.",
    Act = "The job runs once through the per-attempt DI scope.",
    Assert = "The persisted result echoes the injected context's job id and name, proving DI handed the running job's context."
)]
public abstract class JobContextDiResolutionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Handler receives a JobContext by constructor injection matching the running job identity")]
    public async Task Handler_resolves_JobContext_from_constructor_injection()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "context-probe", JobPayload.Json(new ContextProbe("hello"))),
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        // The handler returned the JobContext it was constructor-injected with: identity must match
        // the running job, proving the per-attempt scope resolved a populated JobContext.
        var result = await Services.GetRequiredService<IJobStore>().GetJobResultAsync(enqueued.JobId, null, ct);
        Assert.NotNull(result);
        var typed = JsonJobPayloadSerializer.Default.Deserialize<ContextProbeResult>(
            JobPayload.FromBytes(result!.Format, result.Data.ToArray())
        );

        Assert.Equal(enqueued.JobId, typed.JobIdFromContext);
        Assert.Equal("context-probe", typed.JobNameFromContext);
    }
}
