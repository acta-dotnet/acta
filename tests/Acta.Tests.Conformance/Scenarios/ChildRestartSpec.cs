using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A restart takes back the outcome a child landed with, so the latch that outcome set on its parent goes back to
/// pending. The parent's next replay then waits for the child's next landing, which releases it as a first landing does,
/// and no attempt of the parent is spent on a result the restart cleared. The latch keeps the deadline its wait armed, so
/// a bounded wait is never extended by a restart.
/// </summary>
[ConformanceSpec(
    "child-jobs.restart-rearms-parent-wait",
    "A restarted child puts its parent's wait for it back to pending",
    Area = "ChildJobs",
    Contract = "Restarting a child puts its parent's set latch on it back to pending, so the parent waits for the child's next landing without a charged retry."
)]
[Deploys(
    "job-child-echo",
    "job-child-group-hold",
    "job-child-quick",
    "job-parent-call-child-bounded",
    "job-parent-join-children",
    "job-parent-of-null-then-value-child",
    "job-parent-once-of-null-then-value-child",
    "job-parent-one",
    "job-parent-try-wait-child",
    "lane-step",
    "null-then-value"
)]
public sealed class ChildRestartSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ChildRestartSpec>>
{
    [Fact(
        DisplayName = "A parent with one attempt survives a restart of its child, charged nothing, and succeeds with the child's second result"
    )]
    public async Task A_parent_with_one_attempt_survives_a_restart_of_its_child()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-once-of-null-then-value-child", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));

        var finished = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, finished.Status);
        Assert.Equal(0, finished.FailureCount);
        Assert.Equal(new ChildValueSeen(true, true, 5, null), await Jobs.GetResultAsync<ChildValueSeen>(parent, ct));
    }

    [Fact(
        DisplayName = "A restart of a child leaves its parent's latch on it pending with no value, its deadline kept and its version one higher, and the parent's row as it was"
    )]
    public async Task A_restart_leaves_the_latch_pending_with_no_value_and_its_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartTryWaitParentAsync(ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));

        // The bounded wait stored a deadline on the latch, and the landing set the outcome beside it.
        var landed = await LatchItemAsync(parent, child, ct);
        Assert.Equal(CheckpointStatusCode.Set, landed.Status);
        Assert.Equal(JobPayloadFormat.Json.Id, landed.ValueFormatId);
        Assert.NotNull(landed.ValueByteLength);
        Assert.NotNull(landed.DueAtUtc);
        var landedVersion = (await LatchRowAsync(parent.JobId, child, ct)).Version;
        var parentBefore = await ReadJobAsync(parent.JobId, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);

        var pending = await LatchItemAsync(parent, child, ct);
        Assert.Equal(CheckpointStatusCode.Pending, pending.Status);
        Assert.Equal(0, pending.ValueFormatId);
        Assert.Null(pending.ValueByteLength);
        Assert.Equal(landed.DueAtUtc, pending.DueAtUtc);
        Assert.Equal(landedVersion + 1, (await LatchRowAsync(parent.JobId, child, ct)).Version);
        var parentAfter = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal((parentBefore.Status, parentBefore.Version), (parentAfter.Status, parentAfter.Version));
    }

    [Fact(DisplayName = "A restart of a child leaves an expired latch on its parent expired, and the child's next landing revives nothing")]
    public async Task A_restart_leaves_an_expired_latch_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartTryWaitParentAsync(ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);

        // The parent gave up on the wait: it timed out, cancelled the child, and finished.
        await ChildTimeoutSpec.ExpireChildWaitAsync(Db, parent.JobId, child, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child, ct)).Status);
        var expired = await LatchRowAsync(parent.JobId, child, ct);
        Assert.Equal(CheckpointStatusCode.Expired, expired.Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));

        var latch = await LatchRowAsync(parent.JobId, child, ct);
        Assert.Equal(CheckpointStatusCode.Expired, latch.Status);
        Assert.Equal(expired.Version, latch.Version);
        Assert.Equal(0, latch.ValueFormatId);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
    }

    [Fact(
        DisplayName = "A restart of a child under a finished parent puts the latch back to pending, the child's landing sets it again, and a restarted parent reads that landing"
    )]
    public async Task A_restarted_parent_reads_the_landing_after_its_childs_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-of-null-then-value-child", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.False((await Jobs.GetResultAsync<ChildValueSeen>(parent, ct)).HasValue);

        // A finished parent's latch is reset too: nothing replays it until the parent itself is restarted.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(CheckpointStatusCode.Pending, (await LatchRowAsync(parent.JobId, child, ct)).Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(CheckpointStatusCode.Set, (await LatchRowAsync(parent.JobId, child, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(parent, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(new ChildValueSeen(true, true, 5, null), await Jobs.GetResultAsync<ChildValueSeen>(parent, ct));
    }

    [Fact(
        DisplayName = "An untyped child call whose child was restarted waits again under the deadline it first armed and resumes on the child's next landing"
    )]
    public async Task An_untyped_child_call_waits_again_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-call-child-bounded", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        var armed = (await LatchRowAsync(parent.JobId, child, ct)).DueAtUtc;
        Assert.NotNull(armed);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(JobLookup.ById(child), "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(armed, (await LatchRowAsync(parent.JobId, child, ct)).DueAtUtc);

        // The outcome has no value to read, so only the latch can tell the parent its child is running again.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var waiting = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal((JobStatusCode.Suspended, 0), (waiting.Status, waiting.FailureCount));
        var latch = await LatchRowAsync(parent.JobId, child, ct);
        Assert.Equal(CheckpointStatusCode.Pending, latch.Status);
        Assert.Equal(armed, latch.DueAtUtc);
        Assert.Equal(armed, waiting.NextRunAtUtc);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        var report = await Jobs.GetResultAsync<ChildOutcomeEntry>(parent, ct);
        Assert.Equal((true, false), (report.Succeeded, report.TimedOut));
    }

    [Fact(
        DisplayName = "A parent paused on a bounded child wait, whose child lands and is restarted, runs at once when resumed, waits again uncharged, and is released by the child's next landing"
    )]
    public async Task A_resumed_parent_runs_at_once_after_its_childs_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartTryWaitParentAsync(ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        var armed = (await LatchRowAsync(parent.JobId, child, ct)).DueAtUtc;
        Assert.NotNull(armed);

        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(parent, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(parent, ct: ct)).Action);

        // The parent's next run is still its wait's deadline, which the latch keeps, so resume makes it due now: a
        // resume that found no such deadline would leave it due at the deadline itself.
        var resumed = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, resumed.Status);
        Assert.True(resumed.NextRunAtUtc < armed, $"the resumed parent is due at {resumed.NextRunAtUtc:O}, its deadline {armed:O}");

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var waiting = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal((JobStatusCode.Suspended, 0), (waiting.Status, waiting.FailureCount));
        Assert.Equal(armed, waiting.NextRunAtUtc);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.Equal((true, false), (report.Completed, report.TimedOut));
        Assert.Equal(JobStatusCode.Succeeded, report.Outcome!.Status);
    }

    [Fact(
        DisplayName = "A child restarted after its parent's deadline for it has passed times out on the parent's next replay and is cancelled"
    )]
    public async Task A_child_restarted_past_its_wait_deadline_times_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartTryWaitParentAsync(ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));

        // The deadline passes after the landing; the restart keeps it, so the wait it reopens has already expired.
        await ChildTimeoutSpec.ExpireChildWaitAsync(Db, parent.JobId, child, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.Equal((true, false), (report.TimedOut, report.Completed));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Expired, (await LatchRowAsync(parent.JobId, child, ct)).Status);
        Assert.Equal(0, (await ReadJobAsync(parent.JobId, ct)).FailureCount);
    }

    [Fact(
        DisplayName = "A member of a bounded group restarted after the group's deadline has passed times out on the parent's next replay and is cancelled"
    )]
    public async Task A_group_member_restarted_past_the_group_deadline_times_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "job-parent-join-children",
                JobPayload.Json(new ChildGroupStart(["job-child-quick", "job-child-quick"]))
            ),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var children = await ChildIdsAsync(parent.JobId, ct);
        foreach (var member in children.Values)
        {
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(member, ct));
        }

        // The group's deadline passes after both landed: its stored instant and every member's latch move into the past.
        Assert.NotEqual(0, await WaitTimeoutStaging.ForceGroupWaitDueAsync(Db, parent.JobId, DateTime.UtcNow.AddMinutes(-1), ct));
        foreach (var member in children.Values)
        {
            await ChildTimeoutSpec.ExpireChildWaitAsync(Db, parent.JobId, member, ct);
        }
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(children["c0"]), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildGroupReport>(parent, ct);
        Assert.True(report.TimedOut);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(children["c0"], ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(children["c1"], ct)).Status);
        Assert.Equal(CheckpointStatusCode.Expired, (await LatchRowAsync(parent.JobId, children["c0"], ct)).Status);
    }

    [Fact(DisplayName = "A restarted child that an operator cancels before it lands releases its waiting parent, which reads the cancel")]
    public async Task A_restarted_child_cancelled_before_it_lands_releases_the_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartTryWaitParentAsync(ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(child), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(child), "superseded", ct: ct)).Action);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.Equal((true, false), (report.Completed, report.TimedOut));
        Assert.Equal(JobStatusCode.Cancelled, report.Outcome!.Status);
    }

    [Fact(DisplayName = "A restart rejected for a stale version leaves the parent's latch on the child as it was")]
    public async Task A_rejected_restart_leaves_the_latch_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        var landed = await LatchRowAsync(parent.JobId, child, ct);
        var version = (await ReadJobAsync(child, ct)).Version;

        var restart = await Jobs.RestartAsync(JobLookup.ById(child), expectedVersion: version + 1, ct: ct);
        Assert.Equal(ControlAction.VersionConflict, restart.Action);

        var latch = await LatchRowAsync(parent.JobId, child, ct);
        Assert.Equal(CheckpointStatusCode.Set, latch.Status);
        Assert.Equal(landed.Version, latch.Version);
        Assert.Equal(landed.Value, latch.Value);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct)).Doubled);
    }

    [Fact(
        DisplayName = "A restart rejected because an unfinished ancestor sits in the child's lane leaves the parent's latch on the child as it was"
    )]
    public async Task A_restart_rejected_by_its_lane_leaves_the_latch_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        // The child shares a lane with its grandparent; the parent between them runs in none and takes the child's latch.
        var ancestor = await Jobs.EnqueueAsync(LaneStepRequest("orders", "ancestor"), ct);
        var parent = await Jobs.EnqueueAsync(LaneStepRequest("", "parent") with { ParentJobId = ancestor.JobId }, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(ancestor, ct));
        var child = await Jobs.EnqueueAsync(LaneStepRequest("orders", "child") with { ParentJobId = parent.JobId }, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        var landed = await LatchRowAsync(parent.JobId, child.JobId, ct);
        Assert.Equal(CheckpointStatusCode.Set, landed.Status);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(ancestor, ct: ct)).Action);

        Assert.Equal(ControlAction.Rejected, (await Jobs.RestartAsync(child, ct: ct)).Action);

        var latch = await LatchRowAsync(parent.JobId, child.JobId, ct);
        Assert.Equal(CheckpointStatusCode.Set, latch.Status);
        Assert.Equal(landed.Version, latch.Version);
        Assert.Equal(landed.Value, latch.Value);
    }

    [Fact(
        DisplayName = "On PostgreSQL a restart waits for a transaction that holds its parent's latch on it, then puts the latch that transaction set back to pending"
    )]
    public async Task A_restart_waits_for_a_transaction_holding_the_latch()
    {
        // SQL Server queues the two on the latch's checkpoint lock, and SQLite runs one writer at a time.
        Assert.SkipUnless(Db.Provider == DbProvider.Postgres, "Only PostgreSQL orders a restart and a raise on the latch row itself.");
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);

        // The child ended without its latch set, and a raise of that outcome is under way: it holds the latch it has set.
        await JobStateStaging.SetStatusAsync(Db, child, JobStatusCode.Cancelled, ct);
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var raise = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = raise;
            cmd.CommandText =
                $"UPDATE {Db.Qualify("checkpoints")} SET checkpoint_status_code = 110 /* CheckpointStatusCode.Set */, "
                + "value_format_id = 1 /* JobPayloadFormat.Json */, value = @p_value "
                + "WHERE job_id = @p_parent AND checkpoint_kind_code = 50 /* CheckpointKindCode.Child */ AND checkpoint_name = @p_name";
            AddParameter(cmd, "@p_value", ChildOutcomeEnvelope.Write(child, JobStatusCode.Cancelled, executionNumber: null));
            AddParameter(cmd, "@p_parent", parent.JobId);
            AddParameter(cmd, "@p_name", $"sys.child.{child}");
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        var restart = Jobs.RestartAsync(JobLookup.ById(child), ct: ct);
        await LockWaitProbe.WaitAsync(Db, "_restart_job_latch", LockWait.Row, restart, raise, ct);
        await raise.CommitAsync(ct);
        Assert.Equal(ControlAction.Applied, (await restart).Action);

        var latch = await LatchRowAsync(parent.JobId, child, ct);
        Assert.Equal(CheckpointStatusCode.Pending, latch.Status);
        Assert.Equal(0, latch.ValueFormatId);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct)).Doubled);
    }

    private Task<EnqueueJobResponse> StartTryWaitParentAsync(CancellationToken ct) =>
        Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-try-wait-child", JobPayload.Json(new TryWaitChildStart("job-child-quick"))),
            ct
        );

    private EnqueueJobRequest LaneStepRequest(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane.Length == 0 ? null : lane);

    private async Task<long> OnlyChildAsync(long parentId, CancellationToken ct) =>
        Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parentId).ToListAsync(ct)).Id;

    private async Task<IReadOnlyDictionary<string, long>> ChildIdsAsync(long parentId, CancellationToken ct) =>
        (await Db.From<JobEntity>().Where(j => j.ParentJobId == parentId).ToListAsync(ct)).ToDictionary(
            j => j.IdempotencyKey!,
            j => j.Id,
            StringComparer.Ordinal
        );

    private async Task<CheckpointEntity> LatchRowAsync(long parentId, long childId, CancellationToken ct) =>
        Assert.Single(await ReadSignalsAsync(parentId, ct), s => s.CheckpointName == $"sys.child.{childId}");

    private async Task<ListJobCheckpointsItem> LatchItemAsync(EnqueueJobResponse parent, long childId, CancellationToken ct) =>
        Assert.Single((await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(parent), ct)).Items, c =>
            c.Kind == CheckpointKindCode.Child && c.Name == $"sys.child.{childId}"
        );

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}
