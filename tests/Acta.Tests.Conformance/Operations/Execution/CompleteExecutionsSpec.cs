using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for the batch of plain terminal completions (<c>complete_executions</c>). Every job is claimed straight
/// to Executing and completed without running its handler.
/// </summary>
[ConformanceSpec(
    "execution.complete-executions",
    "A batch of plain terminal completions lands what one completion per job lands",
    Area = "Execution",
    Contract = "A plain terminal completion batch writes what one completion per job writes, answers a parented or laned job PerJob, and touches no row it does not complete."
)]
public sealed class CompleteExecutionsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int LeaseTtl = 30;

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "Two successes and an exhausted failure land the runtime, result, and finished-event rows the per-job write lands")]
    public async Task Batch_lands_the_rows_the_per_job_write_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        // A result with no bytes is still a result: both writes store a zero-length one.
        Func<ClaimedJob, CompleteExecutionsValues>[] shapes =
        [
            job => Plain(job) with { ResultFormatId = JobPayloadFormat.Json.Id, Result = "{\"sum\":3}"u8.ToArray() },
            job => Plain(job) with { ResultFormatId = JobPayloadFormat.Bytes.Id },
            job =>
                Plain(job) with
                {
                    Succeeded = false,
                    EventReasonCode = EventReasonCode.JobUnhandledException,
                    ReasonMessage = "boom",
                    FailureCount = 1,
                },
        ];

        List<(long Single, long Batched)> pairs = [];
        List<CompleteExecutionsValues> batch = [];
        foreach (var shape in shapes)
        {
            var single = await ExecutingAsync(AddNumbers(), workerId, ct);
            Assert.Equal(
                CompleteExecutionAction.Completed,
                (await Execution.CompleteExecutionAsync(PerJob(shape(single), workerId), ct)).Action
            );
            var batched = await ExecutingAsync(AddNumbers(), workerId, ct);
            batch.Add(shape(batched));
            pairs.Add((single.JobId, batched.JobId));
        }

        var answers = await CompleteAsync(workerId, batch, ct);

        Assert.Equal(batch.Count, answers.Count);
        Assert.All(answers.Values, row => Assert.Equal(CompleteExecutionsAction.Completed, row.Action));
        foreach (var (single, batched) in pairs)
        {
            Assert.Equal(await LandedAsync(single, ct), await LandedAsync(batched, ct));
        }
    }

    [Fact(DisplayName = "A job with a parent and a job in a lane are answered PerJob and left Executing")]
    public async Task Parented_and_laned_jobs_are_answered_per_job_and_left_executing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var parent = await Jobs.EnqueueAsync(AddNumbers(), ct);
        var child = await ExecutingAsync(AddNumbers() with { ParentJobId = parent.JobId }, workerId, ct);
        var laned = await ExecutingAsync(
            new EnqueueJobRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("batch", "head")), Lane: "batch"),
            workerId,
            ct
        );
        var before = (await StateAsync(child.JobId, ct), await StateAsync(laned.JobId, ct));

        var answers = await CompleteAsync(workerId, [Plain(child), Plain(laned)], ct);

        Assert.Equal((CompleteExecutionsAction.PerJob, (byte?)JobStatusCode.Executing), Answer(answers, child));
        Assert.Equal((CompleteExecutionsAction.PerJob, (byte?)JobStatusCode.Executing), Answer(answers, laned));
        Assert.Equal(before, (await StateAsync(child.JobId, ct), await StateAsync(laned.JobId, ct)));
    }

    [Fact(DisplayName = "A row another worker leases is NotOwner, a terminal row AlreadyTerminal, and neither is touched")]
    public async Task Foreign_and_terminal_rows_are_answered_and_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        // Negative, so the abandoned lease never matches a real worker's id in the append-only test database.
        var foreign = await ExecutingAsync(AddNumbers(), -workerId, ct);
        var terminal = await ExecutingAsync(AddNumbers(), workerId, ct);
        Assert.Equal(
            CompleteExecutionAction.Completed,
            (await Execution.CompleteExecutionAsync(PerJob(Plain(terminal), workerId), ct)).Action
        );
        var before = (await StateAsync(foreign.JobId, ct), await StateAsync(terminal.JobId, ct));

        var answers = await CompleteAsync(workerId, [Plain(foreign), Plain(terminal)], ct);

        Assert.Equal((CompleteExecutionsAction.NotOwner, (byte?)JobStatusCode.Executing), Answer(answers, foreign));
        Assert.Equal((CompleteExecutionsAction.AlreadyTerminal, (byte?)JobStatusCode.Succeeded), Answer(answers, terminal));
        Assert.Equal(before, (await StateAsync(foreign.JobId, ct), await StateAsync(terminal.JobId, ct)));
    }

    [Fact(DisplayName = "At the failures-only audit level a success writes a finished event only when it answers a recorded failure")]
    public async Task Failures_level_success_is_recorded_only_when_it_answers_a_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await WorkerIdAsync(ct);
        var probe = new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None);
        var quiet = await ExecutingAsync(probe, workerId, ct);
        var failing = await ExecutingAsync(probe, workerId, ct);

        var first = await CompleteAsync(workerId, [Plain(quiet), Plain(failing) with { Succeeded = false, FailureCount = 6 }], ct);

        Assert.All(first.Values, row => Assert.Equal(CompleteExecutionsAction.Completed, row.Action));
        Assert.Equal(0, await CountEventsAsync(quiet.JobId, EventCode.JobExecutionFinished, ct));
        Assert.Equal(1, await CountFinishedWithStatusAsync(failing.JobId, ExecutionStatusCode.Failed, ct));

        // Restart writes nothing at this level, so the failure stays the newest finished event for the success to answer.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(failing.JobId), ct: ct)).Action);
        var retried = await ClaimExecutingAsync(failing.JobId, workerId, ct);
        var second = await CompleteAsync(workerId, [Plain(retried)], ct);

        Assert.Equal(CompleteExecutionsAction.Completed, Assert.Single(second.Values).Action);
        Assert.Equal(1, await CountFinishedWithStatusAsync(failing.JobId, ExecutionStatusCode.Succeeded, ct));
    }

    private EnqueueJobRequest AddNumbers() => new(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));

    // A success with no result, the shape each fact varies.
    private static CompleteExecutionsValues Plain(ClaimedJob job) =>
        new(job.JobId, job.ExecutionNumber, true, JobPayloadFormat.None.Id, ReadOnlyMemory<byte>.Empty, null, null, 5, null, 3600);

    private static CompleteExecutionCommand PerJob(CompleteExecutionsValues values, int workerId) =>
        new(
            values.JobId,
            workerId,
            values.ExecutionNumber,
            values.Succeeded ? CompletionKind.Succeeded : CompletionKind.Failed,
            values.ResultFormatId,
            values.Result,
            values.EventReasonCode,
            values.ReasonMessage,
            values.DurationMs,
            FailureCount: values.FailureCount
        )
        {
            RetentionSeconds = values.RetentionSeconds,
        };

    private async Task<Dictionary<long, CompleteExecutionsRow>> CompleteAsync(
        int workerId,
        IReadOnlyList<CompleteExecutionsValues> batch,
        CancellationToken ct
    ) => (await Execution.CompleteExecutionsAsync(new CompleteExecutionsCommand(workerId, batch), ct)).Rows.ToDictionary(r => r.JobId);

    private static (CompleteExecutionsAction, byte?) Answer(Dictionary<long, CompleteExecutionsRow> answers, ClaimedJob job) =>
        (answers[job.JobId].Action, answers[job.JobId].FinalStatusCode);

    private async Task<int> WorkerIdAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        return Assert.IsType<WorkerEntity>(await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct)).Id;
    }

    private async Task<ClaimedJob> ExecutingAsync(EnqueueJobRequest request, int workerId, CancellationToken ct) =>
        await ClaimExecutingAsync((await Jobs.EnqueueAsync(request, ct)).JobId, workerId, ct);

    private async Task<ClaimedJob> ClaimExecutingAsync(long jobId, int workerId, CancellationToken ct)
    {
        var options = new ClaimOptions(Runtime.RegisteredNamespaceIds[TestNamespace], workerId, MaxBatch: 1, StartExecuting: true);
        return Assert.Single((await Execution.ClaimJobAsync(options, LeaseTtl, jobId, ct)).Jobs);
    }

    // A row is untouched while its version and its finished events stay as they were.
    private async Task<(int Version, int Finished)> StateAsync(long jobId, CancellationToken ct) =>
        ((await ReadJobAsync(jobId, ct)).Version, await CountEventsAsync(jobId, EventCode.JobExecutionFinished, ct));

    // What a completion lands, compared between the two writes: the instants differ, so retention compares as set.
    private async Task<object> LandedAsync(long jobId, CancellationToken ct)
    {
        var job = await ReadJobAsync(jobId, ct);
        var result = await Db.From<ResultEntity>().Where(r => r.JobId == jobId).SingleOrDefaultAsync(ct);
        var finished = await ReadSingleEventAsync(jobId, EventCode.JobExecutionFinished, ct);
        return (
            job.Status,
            job.FailureCount,
            job.LeasedByWorkerId,
            job.LeaseExpiresAtUtc,
            job.RetentionUntilUtc is not null,
            result?.ResultFormatId,
            result is null ? null : Convert.ToBase64String(result.Result),
            (finished.ActorCode, finished.WorkerId, finished.FromStatus, finished.ToStatus, finished.ExecutionStatus),
            (finished.DurationMs, finished.ReasonCode, finished.ReasonMessage)
        );
    }
}
