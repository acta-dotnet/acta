using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

[ConformanceSpec(
    "jobs.note",
    "A handler writes application-authored notes onto the job's own timeline",
    Area = "Execution",
    Contract = "ctx.NoteAsync appends a job.note-recorded event carrying the message, the job's identity, the optional JSON detail, and the attempt that authored it."
)]
public sealed class RecordNoteSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A note for an unknown job throws the stable marker before committing")]
    public async Task Unknown_job_is_rejected()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Services
                .GetRequiredService<Acta.Runtime.Execution>()
                .RecordNoteAsync(-1, 1, "unknown", null, TestContext.Current.CancellationToken)
        );
        Assert.Contains("ACTA:NOTE_UNKNOWN_JOB:", error.Message);
    }

    [Fact(DisplayName = "NoteAsync appends job.note-recorded events carrying the message and the optional detail payload")]
    public async Task Notes_are_appended_to_the_job_timeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));

        var events = await Operations.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: enqueued.JobId, EventCode: EventCode.JobNoteRecorded, PageSize: 50),
            ct
        );

        Assert.Equal(2, events.Items.Count);
        Assert.All(events.Items, e => Assert.Equal(ActorCode.Job, e.ActorCode));

        // The message rides reason_message; the typed overload additionally stores a JSON detail body,
        // and the bare overload leaves the pair encoded as "no detail" (format id 0, NULL body), which
        // surfaces on the read projection as a null DetailText.
        Assert.Contains(events.Items, e => e.ReasonMessage == "plain note");
        Assert.Contains(events.Items, e => e.ReasonMessage == "note with detail");
        var detailed = Assert.Single(events.Items, e => e.DetailText is not null);
        Assert.Contains("gather", detailed.DetailText!);
    }

    [Fact(DisplayName = "A note is filed under the attempt that wrote it, not the attempt its runtime row has reached")]
    public async Task Note_is_filed_under_the_authoring_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        // The row moves on underneath a running handler: a lapsed lease is reclaimed as the next
        // attempt, and cancellation reaches a handler that ignores its token late or never. Standing
        // the row at 8 while attempt 7 writes is that race with the timing taken out of it.
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET execution_number = 8 WHERE job_id = @p_id",
                ct,
                ("@p_id", enqueued.JobId)
            )
        );

        await Services.GetRequiredService<Acta.Runtime.Execution>().RecordNoteAsync(enqueued.JobId, 7, "late note", null, ct);

        var events = await Operations.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: enqueued.JobId, EventCode: EventCode.JobNoteRecorded, PageSize: 50),
            ct
        );

        var note = Assert.Single(events.Items);
        Assert.Equal("late note", note.ReasonMessage);
        Assert.Equal(7, note.ExecutionNumber);

        var runtime = Assert.Single(await Db.From<RuntimeEntity>().Where(r => r.Id == enqueued.JobId).ToListAsync(ct));
        Assert.Equal(8, runtime.ExecutionNumber);
    }
}
