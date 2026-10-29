using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for the execution fence. Every write a handler makes through its context lands only while its attempt
/// still holds the job. Each fact stages the race with its timing taken out: execution 1 holds a context, the job is
/// cancelled, restarted, and claimed as execution 2 (by another worker, or by the same one), and only then does
/// execution 1 write. That write changes nothing, while execution 2 writes and completes as if it had never run.
/// </summary>
[ConformanceSpec(
    "execution.fence",
    "A handler write from an attempt that no longer holds its job changes nothing",
    Area = "Execution",
    Contract = "A write from a superseded attempt, on any worker, changes nothing, while the current attempt's writes and completion land and the stale one stops uncharged."
)]
public sealed class ExecutionFenceSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int LeaseTtlSeconds = 300;
    private const string Receipt = "receipt";
    private static readonly JobControlInput WaitTimedOut = new(null, EventReason.WaitTimedOut, "Parent's child wait timed out.");

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Theory(DisplayName = "A superseded variable write leaves the current attempt's receipt as it wrote it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Variable_set(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        await current.Ctx.SetVariableAsync(Receipt, "current", ct);

        await AssertFencedOutAsync(old, () => old.Ctx.SetVariableAsync(Receipt, "stale", ct), ct);

        Assert.Equal("current", await current.Ctx.GetRequiredVariableAsync<string>(Receipt, ct));
        await current.Ctx.SetVariableAsync(Receipt, "current-2", ct);
        Assert.Equal("current-2", await current.Ctx.GetRequiredVariableAsync<string>(Receipt, ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded get-or-set stores nothing, so the current attempt's first value is its own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Variable_get_or_set(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(old, () => old.Ctx.GetOrSetVariableAsync("seed", () => "stale", ct), ct);

        Assert.Equal("current", await current.Ctx.GetOrSetVariableAsync("seed", () => "current", ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded delete leaves the current attempt's variable in place")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Variable_delete(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        await current.Ctx.SetVariableAsync(Receipt, "current", ct);

        await AssertFencedOutAsync(old, () => old.Ctx.DeleteVariableAsync(Receipt, ct), ct);

        Assert.Equal("current", await current.Ctx.GetRequiredVariableAsync<string>(Receipt, ct));
        Assert.True(await current.Ctx.DeleteVariableAsync(Receipt, ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded progress report leaves the current attempt's progress")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        await current.Ctx.SetProgressAsync(5, ct);

        await AssertFencedOutAsync(old, () => old.Ctx.SetProgressAsync(99, ct), ct);

        await current.Ctx.SetProgressAsync(6, ct);
        var progress = await CheckpointSlot.GetAsync(Execution, current.Fence, CheckpointKindCode.Progress, "sys.progress", ct);
        Assert.NotNull(progress);
        Assert.Equal("6", Encoding.UTF8.GetString(progress.Value));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded at-most-once step start leaves the current attempt's step running, never Interrupted")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task At_most_once_step_start(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = current.Ctx.RunStepAsync(
            "charge",
            async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return 42;
            },
            step => step.AtMostOnce(),
            ct
        );
        await entered.Task.WaitAsync(SpecWaits.Gate, ct);

        var bodyRan = false;
        await AssertFencedOutAsync(
            old,
            () =>
                old.Ctx.RunStepAsync(
                    "charge",
                    _ =>
                    {
                        bodyRan = true;
                        return Task.FromResult(13);
                    },
                    step => step.AtMostOnce(),
                    ct
                ),
            ct
        );
        Assert.False(bodyRan);
        Assert.Equal(StepStatusCode.Pending, (await ReadStepAsync(current.Claim.JobId, "charge", ct)).Status);

        release.TrySetResult();
        Assert.Equal(42, await running.WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal(StepStatusCode.Succeeded, (await ReadStepAsync(current.Claim.JobId, "charge", ct)).Status);
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded step completion at the step's current version still changes nothing")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Step_completion(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? stale = null;

        // Execution 1 starts the step while it holds the job and pauses in the body, before its completion: the step's
        // version is still the one it started at, so only the fence stands between that completion and the row.
        var (old, current) = await SupersedeAsync(
            sameWorker,
            ct,
            async attempt =>
            {
                stale = attempt.Ctx.RunStepAsync(
                    "ship",
                    async token =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return 13;
                    },
                    ct
                );
                await entered.Task.WaitAsync(SpecWaits.Gate, ct);
            }
        );
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(
            old,
            async () =>
            {
                release.TrySetResult();
                await stale!;
            },
            ct
        );

        Assert.Equal(7, await current.Ctx.RunStepAsync("ship", _ => Task.FromResult(7), ct));
        var step = await ReadStepAsync(current.Claim.JobId, "ship", ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(2, step.AttemptNumber);
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded sleep arms no timer, so the current attempt arms its own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sleep_timer(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(old, () => old.Ctx.SleepAsync("nap", TimeSpan.FromHours(1), ct: ct), ct);

        await Assert.ThrowsAsync<SuspendSignal>(() => current.Ctx.SleepAsync("nap", TimeSpan.FromMinutes(10), ct: ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded signal wait arms no slot, so the current attempt arms its own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signal_wait(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(old, () => old.Ctx.WaitSignalAsync("approval", TimeSpan.FromSeconds(1), ct), ct);

        await Assert.ThrowsAsync<SignalSuspendSignal>(() => current.Ctx.WaitSignalAsync("approval", ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded child wait arms no latch, so the current attempt arms its own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_wait(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        var child = await current.Ctx.StartChildAsync(
            "child-a",
            TestNamespace,
            "add-numbers",
            JobPayload.Json(new AddNumbers(1, 1)),
            ct: ct
        );

        await AssertFencedOutAsync(old, () => old.Ctx.WaitChildAsync(child.JobId, ct), ct);

        await Assert.ThrowsAsync<SignalSuspendSignal>(() => current.Ctx.WaitChildAsync(child.JobId, ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    // Execution 1's child wait expired and it is about to cancel the child when it loses the parent. The operator's
    // cancel takes the child down with the parent and the operator restarts both, then execution 2 resets and waits
    // on the same child again. Execution 1's late cancel must leave that child and that wait alone.
    [Theory(DisplayName = "A superseded timed-out child cancel leaves the child the current attempt restarted and waits on")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timed_out_child_cancel(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        EnqueueJobResponse child = null!;
        var (old, current) = await SupersedeAsync(
            sameWorker,
            ct,
            async first =>
                child = await first.Ctx.StartChildAsync(
                    "child-a",
                    TestNamespace,
                    "add-numbers",
                    JobPayload.Json(new AddNumbers(1, 1)),
                    ct: ct
                )
        );
        using var oldAttempt = old;
        using var currentAttempt = current;
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child.JobId), ct: ct)).Action);
        await current.Ctx.ResetStateAsync(ct);
        await Assert.ThrowsAsync<SignalSuspendSignal>(() => current.Ctx.TryWaitChildAsync(child.JobId, TimeSpan.FromHours(1), ct));
        var parentBefore = await DurableStateAsync(current.Claim.JobId, ct);

        await Assert.ThrowsAsync<ExecutionFencedException>(() =>
            Services.GetRequiredService<JobsOperations>().CancelTreeAsync(child.JobId, WaitTimedOut, old.Fence, ct)
        );

        Assert.Equal(JobStatusCode.Ready, (await ReadStateAsync(child.JobId, ct)).Status);
        Assert.Equal(parentBefore, await DurableStateAsync(current.Claim.JobId, ct));
    }

    [Fact(DisplayName = "A timed-out child cancel under the holding attempt's fence cancels the child and its descendants")]
    public async Task Timed_out_child_cancel_under_a_live_fence()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, Runtime.RegisteredNamespaceIds[TestNamespace], ct);
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        using var attempt = await ClaimAsync(parent, worker, ct);
        var child = await attempt.Ctx.StartChildAsync(
            "child-a",
            TestNamespace,
            "add-numbers",
            JobPayload.Json(new AddNumbers(1, 1)),
            ct: ct
        );
        var grandchild = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)), ParentJobId: child.JobId),
            ct
        );

        await Services.GetRequiredService<JobsOperations>().CancelTreeAsync(child.JobId, WaitTimedOut, attempt.Fence, ct);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadStateAsync(child.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadStateAsync(grandchild.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadStateAsync(parent.JobId, ct)).Status);
    }

    [Theory(DisplayName = "A superseded child start creates no child, so the current attempt's child is its own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_start(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(
            old,
            () => old.Ctx.StartChildAsync("child-a", TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(9, 9)), ct: ct),
            ct
        );

        var child = await current.Ctx.StartChildAsync(
            "child-a",
            TestNamespace,
            "add-numbers",
            JobPayload.Json(new AddNumbers(1, 1)),
            ct: ct
        );
        Assert.Equal(EnqueueJobAction.Inserted, child.Action);
        Assert.Equal(child.JobId, Assert.Single((await Execution.GetChildJobIdsAsync(current.Claim.JobId, ct)).ChildJobIds));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded note records nothing on the job's timeline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Note(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;

        await AssertFencedOutAsync(old, () => old.Ctx.NoteAsync("stale note", ct), ct);

        await current.Ctx.NoteAsync("current note", ct);
        var note = Assert.Single(await NotesAsync(current.Claim.JobId, ct));
        Assert.Equal("current note", note.EventMessage);
        Assert.Equal(current.Claim.ExecutionNumber, note.ExecutionNumber);
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Theory(DisplayName = "A superseded state reset clears none of the current attempt's state")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task State_reset(bool sameWorker)
    {
        var ct = TestContext.Current.CancellationToken;
        var (old, current) = await SupersedeAsync(sameWorker, ct);
        using var oldAttempt = old;
        using var currentAttempt = current;
        await current.Ctx.SetVariableAsync(Receipt, "current", ct);

        await AssertFencedOutAsync(old, () => old.Ctx.ResetStateAsync(ct), ct);

        Assert.Equal("current", await current.Ctx.GetRequiredVariableAsync<string>(Receipt, ct));
        await current.Ctx.ResetStateAsync(ct);
        Assert.False(await current.Ctx.ExistsVariableAsync(Receipt, ct));
        await AssertCompletionAcceptedAsync(current, ct);
    }

    [Fact(DisplayName = "A worker's superseded attempt stops at its fenced write with no completion, and its next attempt lands")]
    public async Task Fenced_attempt_stops_like_a_lost_lease_on_the_same_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "execution-fence-probe", JobPayload.None), ct);
        var jobId = enqueued.JobId;

        var stale = Runtime.RunOnceAsync(enqueued, ct);
        await ExecutionFenceProbe.Started(jobId, 1).WaitAsync(SpecWaits.Gate, ct);
        await CancelAndRestartAsync(jobId, ct);
        var cancelled = await FirstAttemptEventIdsAsync(jobId, ct);
        var next = Runtime.RunOnceAsync(enqueued, ct);
        await ExecutionFenceProbe.Started(jobId, 2).WaitAsync(SpecWaits.Gate, ct);

        ExecutionFenceProbe.Release(jobId, 1);
        Assert.IsType<ExecutionFencedException>(await ExecutionFenceProbe.Wrote(jobId, 1).WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal(RunOnceStatus.NothingClaimed, await stale.WaitAsync(SpecWaits.Gate, ct));
        await AssertSupersededLeftNoTraceAsync(jobId, cancelled, ct);

        ExecutionFenceProbe.Release(jobId, 2);
        Assert.Null(await ExecutionFenceProbe.Wrote(jobId, 2).WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal(RunOnceStatus.Completed, await next.WaitAsync(SpecWaits.Gate, ct));
        var settled = await ReadStateAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, settled.Status);
        Assert.Equal(0, settled.FailureCount);
        Assert.Equal("\"attempt-2\"", await ReceiptAsync(jobId, ct));
    }

    [Fact(DisplayName = "A worker's superseded attempt stops at its fenced write while another worker's attempt holds the job and lands")]
    public async Task Fenced_attempt_stops_like_a_lost_lease_beside_another_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "execution-fence-probe", JobPayload.None), ct);
        var jobId = enqueued.JobId;

        var stale = Runtime.RunOnceAsync(enqueued, ct);
        await ExecutionFenceProbe.Started(jobId, 1).WaitAsync(SpecWaits.Gate, ct);
        await CancelAndRestartAsync(jobId, ct);
        var cancelled = await FirstAttemptEventIdsAsync(jobId, ct);
        var otherWorker = await OtherWorkerAsync(ct);
        var claimed = Assert.Single(
            await Execution.ClaimJobAsync(Runtime.RegisteredNamespaceIds[TestNamespace], otherWorker, LeaseTtlSeconds, enqueued, ct)
        );
        var current = new ExecutionFence(jobId, claimed.ExecutionNumber, otherWorker);
        await CheckpointSlot.SetAsync(Execution, current, CheckpointKindCode.Variable, Receipt, JobPayload.Json("current"), ct);

        ExecutionFenceProbe.Release(jobId, 1);
        Assert.IsType<ExecutionFencedException>(await ExecutionFenceProbe.Wrote(jobId, 1).WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal(RunOnceStatus.NothingClaimed, await stale.WaitAsync(SpecWaits.Gate, ct));
        Assert.Equal("\"current\"", await ReceiptAsync(jobId, ct));
        await AssertSupersededLeftNoTraceAsync(jobId, cancelled, ct);

        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (await Execution.CompleteAsync(otherWorker, CompletionTestOps.Landing(claimed), ct)).Action
        );
        var settled = await ReadStateAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, settled.Status);
        Assert.Equal(0, settled.FailureCount);
    }

    /// <summary>One claimed execution: the context its handler would get, over an attempt entry the fence can cancel.</summary>
    private sealed record Attempt(ClaimedJob Claim, int WorkerId, InFlightJob Entry, RuntimeJobContext Ctx) : IDisposable
    {
        public ExecutionFence Fence => new(Claim.JobId, Claim.ExecutionNumber, WorkerId);

        public void Dispose() => Entry.Dispose();
    }

    // Execution 1 is claimed and holds a context; beforeMove runs while it still holds the job. The job is then
    // cancelled, restarted, and claimed as execution 2 by another worker, or by the same one.
    private async Task<(Attempt Old, Attempt Current)> SupersedeAsync(
        bool sameWorker,
        CancellationToken ct,
        Func<Attempt, Task>? beforeMove = null
    )
    {
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, Runtime.RegisteredNamespaceIds[TestNamespace], ct);
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var old = await ClaimAsync(enqueued, worker, ct);
        if (beforeMove is not null)
        {
            await beforeMove(old);
        }

        await CancelAndRestartAsync(enqueued.JobId, ct);
        var current = await ClaimAsync(enqueued, sameWorker ? worker : await OtherWorkerAsync(ct), ct);
        Assert.Equal(old.Claim.ExecutionNumber + 1, current.Claim.ExecutionNumber);
        return (old, current);
    }

    private async Task<Attempt> ClaimAsync(EnqueueJobResponse job, int workerId, CancellationToken ct)
    {
        var claim = Assert.Single(
            await Execution.ClaimJobAsync(Runtime.RegisteredNamespaceIds[TestNamespace], workerId, LeaseTtlSeconds, job, ct)
        );
        var entry = new InFlightJob(claim, leaseRunway: null, CancellationToken.None);
        var ctx = new RuntimeJobContext(
            claim,
            jobName: "add-numbers",
            namespaceName: TestNamespace,
            namespaceId: Runtime.RegisteredNamespaceIds[TestNamespace],
            leaseTtlSeconds: LeaseTtlSeconds,
            jobOperations: Services.GetRequiredService<JobsOperations>(),
            signals: Services.GetRequiredService<SignalsOperations>(),
            alerts: Services.GetRequiredService<AlertsOperations>(),
            executionStore: Execution,
            serializers: Services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            locks: Services.GetRequiredService<LocksOperations>(),
            firing: null,
            deadlineAtUtc: null,
            cancellationToken: entry.AttemptToken,
            inFlightJob: entry,
            stepRetryDefaults: new StepRetryDefaults(3, 1, 60, 2m, 0m),
            workerId: workerId,
            clock: Services.GetRequiredService<IActaClock>()
        );
        return new Attempt(claim, workerId, entry, ctx);
    }

    private async Task CancelAndRestartAsync(long jobId, CancellationToken ct)
    {
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(jobId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(jobId), ct: ct)).Action);
    }

    private async Task<int> OtherWorkerAsync(CancellationToken ct) =>
        (await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "fence-host", "v1", null, null, 4245, 4, ct)).WorkerId;

    // The superseded attempt's write is refused, marks its context fenced, cancels its token, and leaves every durable
    // row of the job exactly as the current attempt left it.
    private async Task AssertFencedOutAsync(Attempt old, Func<Task> write, CancellationToken ct)
    {
        var before = await DurableStateAsync(old.Claim.JobId, ct);

        await Assert.ThrowsAsync<ExecutionFencedException>(write);

        Assert.True(old.Ctx.Fenced);
        Assert.True(old.Ctx.CancellationToken.IsCancellationRequested);
        Assert.Equal(before, await DurableStateAsync(old.Claim.JobId, ct));
    }

    private async Task AssertCompletionAcceptedAsync(Attempt current, CancellationToken ct)
    {
        var landed = await Execution.CompleteAsync(current.WorkerId, CompletionTestOps.Landing(current.Claim), ct);
        Assert.Equal(CompleteExecutionsAction.Completed, landed.Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadStateAsync(current.Claim.JobId, ct)).Status);
    }

    // Execution 1's events, which the operator cancel writes when it takes the job from that attempt.
    private async Task<long[]> FirstAttemptEventIdsAsync(long jobId, CancellationToken ct) =>
        [
            .. (await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct))
                .Where(e => e.ExecutionNumber == 1)
                .Select(e => e.Id)
                .Order(),
        ];

    // Execution 1 recorded nothing past what the cancel wrote, so no outcome of its own, and charged no retry, and the
    // row is still with execution 2.
    private async Task AssertSupersededLeftNoTraceAsync(long jobId, long[] cancelled, CancellationToken ct)
    {
        Assert.Equal(cancelled, await FirstAttemptEventIdsAsync(jobId, ct));
        var state = await ReadStateAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Executing, state.Status);
        Assert.Equal(2, state.ExecutionNumber);
        Assert.Equal(0, state.FailureCount);
    }

    // Every durable row a handler can write for the job, rendered so two readings compare whole.
    private async Task<string> DurableStateAsync(long jobId, CancellationToken ct)
    {
        var checkpoints = await Db.From<CheckpointEntity>().Where(c => c.JobId == jobId).ToListAsync(ct);
        var steps = await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct);
        var children = (await Execution.GetChildJobIdsAsync(jobId, ct)).ChildJobIds;
        var notes = await NotesAsync(jobId, ct);
        var state = await ReadStateAsync(jobId, ct);
        List<string> lines =
        [
            .. checkpoints
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.CheckpointName, StringComparer.Ordinal)
                .Select(c =>
                    $"checkpoint {c.Kind} {c.CheckpointName} {c.Status} {Convert.ToHexString(c.Value ?? [])} v{c.Version} {c.DueAtUtc:o}"
                ),
            .. steps
                .OrderBy(s => s.StepName, StringComparer.Ordinal)
                .Select(s => $"step {s.StepName} {s.Status} a{s.AttemptNumber} v{s.Version} {s.NextRetryAtUtc:o} {s.FailureCode}"),
            .. children.Order().Select(id => $"child {id}"),
            $"notes {notes.Count}",
            $"job {state.Status} e{state.ExecutionNumber} w{state.LeasedByWorkerId} v{state.Version} {state.NextRunAtUtc:o}",
        ];
        return string.Join("\n", lines);
    }

    private async Task<IReadOnlyList<EventEntity>> NotesAsync(long jobId, CancellationToken ct) =>
        await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == EventCode.JobNoteRecorded)
            .ToListAsync(ct);

    private async Task<JobStateEntity> ReadStateAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<JobStateEntity>().Where(r => r.Id == jobId).ToListAsync(ct));

    private async Task<StepEntity> ReadStepAsync(long jobId, string name, CancellationToken ct) =>
        Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == name).ToListAsync(ct));

    private async Task<string?> ReceiptAsync(long jobId, CancellationToken ct)
    {
        var row = await CheckpointSlot.GetAsync(Execution, new ExecutionFence(jobId, 0, 0), CheckpointKindCode.Variable, Receipt, ct);
        return row is null ? null : Encoding.UTF8.GetString(row.Value);
    }
}
