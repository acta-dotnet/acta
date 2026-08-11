using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

[ConformanceSpec(
    "jobs.note",
    "A handler writes application-authored notes onto the job's own timeline",
    Area = "Execution",
    Contract = "ctx.NoteAsync appends a job.note event carrying the message, the job's denormalized identity, and the optional JSON detail.",
    Arrange = "A probe job calls NoteAsync once without detail and once with a typed detail payload.",
    Act = "The job runs to completion on a real worker runtime.",
    Assert = "Two job.note events exist for the job, actor Job, one with a JSON detail body and one with none."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.RecordJobNoteAsync))]
public abstract class JobNoteSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "NoteAsync appends job.note events carrying the message and the optional detail payload")]
    public async Task Notes_are_appended_to_the_job_timeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));

        var events = await Operations.Ledger.ListEventsAsync(
            new ListJobEventsQuery(JobId: enqueued.JobId, EventCode: JobEventCode.JobNoteRecorded, PageSize: 50),
            ct
        );

        Assert.Equal(2, events.Items.Count);
        Assert.All(events.Items, e => Assert.Equal(JobActorCode.Job, e.ActorCode));

        // The message rides reason_message; the typed overload additionally stores a JSON detail body,
        // and the bare overload leaves the pair encoded as "no detail" (format id 0, NULL body), which
        // surfaces on the read projection as a null DetailText.
        Assert.Contains(events.Items, e => e.ReasonMessage == "plain note");
        Assert.Contains(events.Items, e => e.ReasonMessage == "note with detail");
        var detailed = Assert.Single(events.Items, e => e.DetailText is not null);
        Assert.Contains("gather", detailed.DetailText!);
    }
}
