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
    Contract = "ctx.NoteAsync appends a job.note-recorded event with the message, optional JSON details, and the writing attempt and worker, while that attempt holds the job."
)]
public sealed class RecordNoteSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A note for an unknown job is refused like one from an attempt that no longer holds its job")]
    public async Task Unknown_job_is_refused()
    {
        await Assert.ThrowsAsync<ExecutionFencedException>(() =>
            Services
                .GetRequiredService<ExecutionOperations>()
                .RecordNoteAsync(new ExecutionFence(-1, 1, 0), "unknown", null, TestContext.Current.CancellationToken)
        );
    }

    [Fact(DisplayName = "NoteAsync appends job.note-recorded events carrying the message and the optional detail payload")]
    public async Task Notes_are_appended_to_the_job_timeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);

        var events = await Operations.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: enqueued.JobId, EventCode: EventCode.JobNoteRecorded, PageSize: 50),
            ct
        );

        Assert.Equal(2, events.Items.Count);

        // The message rides event_message; the typed overload additionally stores a JSON details body,
        // and the bare overload leaves the pair encoded as "no details" (format id 0, NULL body), which
        // surfaces on the read projection as a null DetailsText.
        Assert.Contains(events.Items, e => e.Message == "plain note");
        Assert.Contains(events.Items, e => e.Message == "note with detail");
        var detailed = Assert.Single(events.Items, e => e.DetailsText is not null);
        Assert.Contains("gather", detailed.DetailsText!);
    }

    [Fact(DisplayName = "A note stores the id of the worker whose attempt wrote it")]
    public async Task Note_stores_its_worker_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        var stored = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == enqueued.JobId)
            .ToListAsync(ct);
        var finished = Assert.Single(stored, e => e.EventCode == EventCode.JobExecutionSucceeded);
        Assert.NotEqual(0, finished.WorkerId);
        var notes = stored.Where(e => e.EventCode == EventCode.JobNoteRecorded).ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, note => Assert.Equal(finished.WorkerId, note.WorkerId));
    }

    [Fact(DisplayName = "A note from an attempt the row has moved past is refused, and the current attempt's note is filed under it")]
    public async Task Note_from_a_superseded_attempt_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-note", ct);

        // The row moves on underneath a running handler: a lapsed lease is reclaimed as the next attempt, and
        // cancellation reaches a handler that ignores its token late or never. Standing the row at 8 while attempt 7
        // writes is that race with the timing taken out of it.
        var current = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct, executionNumber: 8);

        await Assert.ThrowsAsync<ExecutionFencedException>(() =>
            execution.RecordNoteAsync(current with { ExecutionNumber = 7 }, "late note", null, ct)
        );
        await execution.RecordNoteAsync(current, "current note", null, ct);

        var events = await Operations.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: enqueued.JobId, EventCode: EventCode.JobNoteRecorded, PageSize: 50),
            ct
        );
        var note = Assert.Single(events.Items);
        Assert.Equal("current note", note.Message);
        Assert.Equal(8, note.ExecutionNumber);
    }
}
