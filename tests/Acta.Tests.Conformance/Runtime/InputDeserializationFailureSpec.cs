using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// A stored payload that no longer matches its handler contract is an attempt failure, not an executor
/// escape. The runtime must settle the Executing row and persist the exception on the job timeline so
/// operators can diagnose contract drift from the job detail page.
/// </summary>
[ConformanceSpec(
    "input-deserialization.failure-timeline",
    "Input deserialization failures settle the attempt and stay on the timeline",
    Area = "Execution",
    Contract = "A payload deserialization exception follows normal failure and retry semantics and records an operator-readable reason on JobExecutionFinished.",
    Arrange = "An add-numbers job is enqueued with malformed JSON input.",
    Act = "The runtime claims the job and attempts to deserialize its stored payload.",
    Assert = "The job leaves Executing, re-arms Ready within its retry budget, and its finished event identifies the deserialization exception."
)]
public abstract class InputDeserializationFailureSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Malformed input leaves Executing and records the deserialization exception")]
    public async Task Malformed_input_is_settled_and_recorded_on_the_job_timeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var malformed = JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"left\":1} trailing"u8);
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", malformed), ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        var job = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(job);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Equal((short)1, job.FailureCount);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = Assert.Single(events, e => e.JobEventCode == JobEventCode.JobExecutionFinished);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, finished.JobEventReasonCode);
        Assert.StartsWith("Input deserialization failed (JsonException):", finished.ReasonMessage);
    }
}
