using System.Globalization;
using System.Text;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for bounded group waits: one absolute deadline is stored for the whole group and reused
/// by every child and every replay, so the budget never restarts. An expired group cancels only its
/// unfinished members and their subtrees, leaves children outside the group alone, and always leaves
/// the parent running.
/// </summary>
[ConformanceSpec(
    "child-jobs.group-wait-timeout",
    "A bounded group wait spends one stored deadline across every child and replay",
    Area = "ChildJobs",
    Contract = "A bounded group wait stores one absolute deadline, spends it across all children and replays, and cancels only unfinished members on expiry.",
    Arrange = "Parents wait on child groups with a 30-minute bound so only a deliberate rewind of the stored group deadline can expire one.",
    Act = "The runtime ticks parents and children around the persisted group deadline, with members landing before it, after it, and never.",
    Assert = "The stored deadline never moves, unfinished members and their subtrees are cancelled with job.wait-timed-out, and the parent keeps running."
)]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.WaitSignalAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CheckpointSlotAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobLineageMapAsync))]
public abstract class ChildGroupTimeoutSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string DeadlinePrefix = "sys.wait-group.";

    [Fact(DisplayName = "A group that finishes before its deadline returns what the unbounded form returns")]
    public async Task Group_before_the_deadline_matches_the_unbounded_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var bounded = await StartGroupParentAsync("job-parent-try-wait-children", ["job-child-quick", "job-child-quick"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(bounded, ct));

        // Exactly one deadline slot for the whole group, whatever the group holds.
        var deadline = Assert.Single(await ReadDeadlineSlotsAsync(bounded.JobId, ct));
        Assert.Equal(JobPayloadFormat.Json.Id, deadline.ValueFormatId);

        await RunChildrenAsync(bounded.JobId, RunOnceOutcome.Completed, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(bounded, ct));

        var unbounded = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "job-parent-wait-children-unbounded",
                JobPayload.Json(new UnboundedChildGroupStart(["job-child-quick", "job-child-quick"]))
            ),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(unbounded, ct));
        await RunChildrenAsync(unbounded.JobId, RunOnceOutcome.Completed, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(unbounded, ct));

        // Same statuses, same order, same flags: the bound is invisible when nothing runs out of time.
        var boundedReport = await Jobs.GetResultAsync<ChildGroupReport>(bounded, ct);
        var unboundedReport = await Jobs.GetResultAsync<ChildGroupReport>(unbounded, ct);
        Assert.False(boundedReport!.TimedOut);
        Assert.True(boundedReport.Succeeded);
        Assert.Equal(
            unboundedReport!.Children.Select(c => (c.Status, c.TimedOut, c.Succeeded)),
            boundedReport.Children.Select(c => (c.Status, c.TimedOut, c.Succeeded))
        );
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(bounded.JobId, ct)).Status);
        Assert.Equal(0, await CountReasonAsync(bounded.JobId, JobEventReasonCode.JobWaitTimedOut, ct));
        Assert.Empty(await ReadDeadlineSlotsAsync(unbounded.JobId, ct));
    }

    [Fact(DisplayName = "An expired group cancels its unfinished member's subtree and spares the child it never awaited")]
    public async Task Expired_group_cancels_only_the_unfinished_members()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-parent-try-wait-children-subtree", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var children = await ChildIdsAsync(parent.JobId, ct);
        var deep = children["deep"];
        var quick = children["quick"];
        var sibling = children["sibling"];
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(deep, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(quick, ct));
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(sibling, ct));
        var grandchild = await OnlyChildAsync(deep, ct);

        await ExpireGroupWaitAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        // The parent is the one thing a group timeout must never touch.
        var parentJob = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, parentJob.Status);
        Assert.Equal(0, parentJob.FailureCount);
        Assert.Equal(1, await CountVariableAsync(parent.JobId, "ran.after", ct));
        Assert.Equal(0, await CountEventsAsync(parent.JobId, EventCode.JobCancelled, ct));

        // The member that landed in time keeps its own outcome; the one that did not reports the timeout.
        var report = await Jobs.GetResultAsync<ChildGroupReport>(parent, ct);
        Assert.True(report!.TimedOut);
        Assert.False(report.Succeeded);
        Assert.True(report.Children[0].TimedOut);
        Assert.Equal(deep, report.Children[0].ChildJobId);
        Assert.False(report.Children[1].TimedOut);
        Assert.True(report.Children[1].Succeeded);
        Assert.Equal(quick, report.Children[1].ChildJobId);

        foreach (var cancelledId in (long[])[deep, grandchild])
        {
            Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(cancelledId, ct)).Status);
            Assert.Equal(1, await CountEventsAsync(cancelledId, EventCode.JobCancelled, ct));
            Assert.Equal(
                JobEventReasonCode.JobWaitTimedOut,
                (await ReadLatestEventAsync(cancelledId, EventCode.JobCancelled, ct)).ReasonCode
            );
        }

        // The sibling was started by the same parent but never joined the group, so nothing reaches it.
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(sibling, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(sibling, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "A crash replay reuses the stored group deadline byte for byte and extends no slot")]
    public async Task Replay_reuses_the_stored_group_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartGroupParentAsync("job-parent-try-wait-children", ["job-wait-signal", "job-wait-signal"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var firstLatch = Assert.Single(await ReadLatchesAsync(parent.JobId, ct));

        // Spend most of the budget, so a replay that recomputed the deadline instead of reading it back
        // would arm the next member 30 minutes out rather than inside this minute.
        var deadlineAtUtc = DateTime.UtcNow.AddSeconds(60);
        await SetGroupDeadlineAsync(parent.JobId, deadlineAtUtc, ct);
        var stored = Assert.Single(await ReadDeadlineSlotsAsync(parent.JobId, ct));

        // Crash-replay the parent from the top, twice, with a member landing in between so the second
        // replay has to arm a slot no earlier pass touched.
        await SetJobStatusReadyAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        await CompleteHeldChildAsync(firstLatch.Name, ct);
        await SetJobStatusReadyAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        // Byte-identical and unversioned: the replays read the deadline back, they never rewrote it.
        var replayed = Assert.Single(await ReadDeadlineSlotsAsync(parent.JobId, ct));
        Assert.Equal(stored.Value, replayed.Value);
        Assert.Equal(stored.Version, replayed.Version);

        // The slot armed before the budget was spent keeps its own due, and the one armed after the
        // replays counts down to the deadline as it now stands.
        var latches = await ReadLatchesAsync(parent.JobId, ct);
        Assert.Equal(2, latches.Count);
        Assert.Equal(firstLatch.DueAtUtc, latches.Single(l => l.Name == firstLatch.Name).DueAtUtc);
        AssertWithinGroupDeadline(latches.Single(l => l.Name != firstLatch.Name), deadlineAtUtc);
    }

    [Fact(DisplayName = "A member armed on a subsequent pass counts down to the group deadline, not from its own arm")]
    public async Task Duration_does_not_restart_for_a_member_armed_after_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartGroupParentAsync("job-parent-try-wait-children", ["job-wait-signal", "job-wait-signal"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var first = Assert.Single(await ReadLatchesAsync(parent.JobId, ct));

        // Stand in for most of the budget having been spent: the stored deadline is now a minute out.
        // Without that the two arms would sit milliseconds apart and a restarting duration would be
        // indistinguishable from a counted-down one.
        var deadlineAtUtc = DateTime.UtcNow.AddSeconds(60);
        await SetGroupDeadlineAsync(parent.JobId, deadlineAtUtc, ct);

        await CompleteHeldChildAsync(first.Name, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var latches = await ReadLatchesAsync(parent.JobId, ct);
        var second = latches.Single(l => l.Name != first.Name);

        // The whole point of the slice: the second member's expiration lands on the group's instant
        // rather than a fresh 30 minutes from its own arm.
        AssertWithinGroupDeadline(second, deadlineAtUtc);
        Assert.True(
            deadlineAtUtc - second.DueAtUtc!.Value <= TimeSpan.FromSeconds(5),
            $"the second member gave up at {second.DueAtUtc} well before the group deadline {deadlineAtUtc}."
        );

        // The member armed first keeps the due it was armed with, even though the remaining time shrank
        // under it: a stored expiration is never rewritten, and both were derived from the same instant.
        Assert.Equal(first.DueAtUtc, latches.Single(l => l.Name == first.Name).DueAtUtc);
    }

    [Fact(DisplayName = "A member first armed after the deadline passed resolves TimedOut on its next tick")]
    public async Task An_already_expired_deadline_resolves_a_freshly_armed_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartGroupParentAsync("job-parent-try-wait-children", ["job-wait-signal", "job-wait-signal"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var first = Assert.Single(await ReadLatchesAsync(parent.JobId, ct));
        await ExpireGroupWaitAsync(parent.JobId, ct);

        // The first member expires immediately: its slot was armed by an earlier pass. The second was
        // never armed, so the arbiter suspends it once before it can expire, which is the accepted cost
        // of a wait always carrying a positive bound.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var second = (await ReadLatchesAsync(parent.JobId, ct)).Single(l => l.Name != first.Name);
        Assert.Equal(JobCheckpointStatusCode.Pending, second.Status);
        Assert.NotNull(second.DueAtUtc);

        await RewindLatchAsync(parent.JobId, second.Name, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildGroupReport>(parent, ct);
        Assert.True(report!.TimedOut);
        Assert.All(report.Children, c => Assert.True(c.TimedOut));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A replay over an expired group re-runs every member cancel as a no-op")]
    public async Task Replay_re_runs_the_group_cancel_as_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartGroupParentAsync("job-parent-try-wait-children", ["job-wait-signal", "job-wait-signal"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var first = Assert.Single(await ReadLatchesAsync(parent.JobId, ct));
        await ExpireGroupWaitAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var second = (await ReadLatchesAsync(parent.JobId, ct)).Single(l => l.Name != first.Name);
        await RewindLatchAsync(parent.JobId, second.Name, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var members = (await ChildIdsAsync(parent.JobId, ct)).Values.ToArray();
        Assert.Equal(2, members.Length);
        foreach (var member in members)
        {
            Assert.Equal(1, await CountEventsAsync(member, EventCode.JobCancelled, ct));
        }

        // Crash-replay the whole handler over slots that are already Expired: it re-derives the same
        // timeouts and re-runs the cancels, which must add nothing to the ledger.
        await SetJobStatusReadyAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        foreach (var member in members)
        {
            Assert.Equal(1, await CountEventsAsync(member, EventCode.JobCancelled, ct));
            Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(member, ct)).Status);
        }
        Assert.True((await Jobs.GetResultAsync<ChildGroupReport>(parent, ct))!.TimedOut);
    }

    [Fact(DisplayName = "A bounded ExecuteChild reports the timeout on its job outcome")]
    public async Task Bounded_execute_child_surfaces_the_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-execute-child-bounded", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(child, ct));

        // One child needs no group deadline slot: the latch's own expiration already is the one instant.
        Assert.Empty(await ReadDeadlineSlotsAsync(parent.JobId, ct));

        await RewindLatchAsync(parent.JobId, $"sys.child.{child}", ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildExecuteReport>(parent, ct);
        Assert.True(report!.IsTimedOut);
        Assert.False(report.IsSuccess);
        Assert.False(report.IsCancelled);
        Assert.Equal(JobStatusCode.Cancelled, report.TerminalStatus);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A bounded Join reports the timeout on the member that did not land")]
    public async Task Bounded_join_surfaces_the_timeout() => await AssertWrapperTimesOutAsync("job-parent-join-bounded");

    [Fact(DisplayName = "A bounded Parallel reports the timeout while keeping its branch keying")]
    public async Task Bounded_parallel_surfaces_the_timeout() => await AssertWrapperTimesOutAsync("job-parent-parallel-bounded");

    [Fact(DisplayName = "A bounded Map reports the timeout while keeping its item keying")]
    public async Task Bounded_map_surfaces_the_timeout() => await AssertWrapperTimesOutAsync("job-parent-map-bounded");

    [Fact(DisplayName = "A lineage map shows the child wait a parent is parked on, with its deadline when bounded")]
    public async Task Lineage_map_shows_the_active_child_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var bounded = await StartGroupParentAsync("job-parent-try-wait-children", ["job-wait-signal"], ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(bounded, ct));

        var latch = Assert.Single(await ReadLatchesAsync(bounded.JobId, ct));
        var boundedMap = await Jobs.GetLineageMapAsync(bounded, ct: ct);
        Assert.NotNull(boundedMap!.ActiveWait);
        Assert.Equal(JobCheckpointKindCode.ChildLatch, boundedMap.ActiveWait!.Kind);
        Assert.Equal(latch.Name, boundedMap.ActiveWait.Name);
        Assert.Equal(latch.DueAtUtc, boundedMap.ActiveWait.DueAtUtc);

        var unbounded = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(unbounded, ct));

        var unboundedMap = await Jobs.GetLineageMapAsync(unbounded, ct: ct);
        Assert.NotNull(unboundedMap!.ActiveWait);
        Assert.Equal(JobCheckpointKindCode.ChildLatch, unboundedMap.ActiveWait!.Kind);
        Assert.Null(unboundedMap.ActiveWait.DueAtUtc);
    }

    // ---------- helpers ----------

    // Every bounded wrapper resolves the same way: one member is let through, the group deadline is
    // rewound past, and the parent resumes reporting a group timeout beside the member that landed.
    private async Task AssertWrapperTimesOutAsync(string parentJobName)
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, parentJobName, JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var members = await ChildIdsAsync(parent.JobId, ct);
        Assert.Equal(2, members.Count);
        var fast = members.Single(kv => kv.Key.EndsWith("fast", StringComparison.Ordinal)).Value;
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(JobLookup.ById(fast), "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(fast, ct));
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        await ExpireGroupWaitAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildGroupReport>(parent, ct);
        Assert.True(report!.TimedOut);
        Assert.False(report.Succeeded);
        Assert.True(report.Children[0].Succeeded);
        Assert.False(report.Children[0].TimedOut);
        Assert.True(report.Children[1].TimedOut);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(fast, ct)).Status);
    }

    private async Task<JobEnqueueOutcome> StartGroupParentAsync(string parentJobName, string[] childJobNames, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, parentJobName, JobPayload.Json(new ChildGroupStart(childJobNames))),
            ct
        );

    private async Task RunChildrenAsync(long parentId, RunOnceOutcome expected, CancellationToken ct)
    {
        foreach (var childId in (await ChildIdsAsync(parentId, ct)).Values)
        {
            Assert.Equal(expected, await Runtime.RunOnceAsync(childId, ct));
        }
    }

    private async Task<long> OnlyChildAsync(long parentId, CancellationToken ct) =>
        Assert.Single(await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct)).Id;

    private async Task<IReadOnlyDictionary<string, long>> ChildIdsAsync(long parentId, CancellationToken ct)
    {
        var children = await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct);
        return children.ToDictionary(j => j.DeduplicationKey!, j => j.Id, StringComparer.Ordinal);
    }

    private async Task<int> CountReasonAsync(long jobId, JobEventReasonCode reason, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.ReasonCode == reason).CountAsync(ct);

    private async Task<IReadOnlyList<JobCheckpoint>> ReadDeadlineSlotsAsync(long jobId, CancellationToken ct)
    {
        var variables = await Db.From<JobCheckpoint>()
            .Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Variable)
            .ToListAsync(ct);
        return [.. variables.Where(c => c.Name.StartsWith(DeadlinePrefix, StringComparison.Ordinal))];
    }

    private async Task<IReadOnlyList<JobCheckpoint>> ReadLatchesAsync(long jobId, CancellationToken ct) =>
        [.. (await ReadSignalsAsync(jobId, ct)).Where(c => c.Kind == JobCheckpointKindCode.ChildLatch)];

    // An arm floors the remaining time to whole seconds, then the store stamps the due against a clock
    // read a round trip after the one the remaining was measured from. So a member's due sits at most a
    // second before the group deadline and at most one round trip past it, on every arm, without ever
    // accumulating. The tolerance is that round trip and nothing else.
    private static void AssertWithinGroupDeadline(JobCheckpoint latch, DateTime deadlineAtUtc)
    {
        Assert.NotNull(latch.DueAtUtc);
        Assert.True(
            latch.DueAtUtc!.Value <= deadlineAtUtc.AddSeconds(1),
            $"{latch.Name} due {latch.DueAtUtc} outlived the group deadline {deadlineAtUtc} by more than one round trip."
        );
    }

    // The stored deadline is UTC ticks, so the assertion reads exactly the bytes the handler wrote.
    private static DateTime DeadlineOf(JobCheckpoint slot) =>
        new(long.Parse(Encoding.UTF8.GetString(slot.Value!), CultureInfo.InvariantCulture), DateTimeKind.Utc);

    // The same staging the public ForceGroupWaitTimeoutDueAsync helper performs, called through the one
    // implementation both share: this spec drives a WorkerRuntime rather than an IActaTestHost, so it
    // reaches the code rather than the facade. ScenarioSessionSpec covers the public helper itself.
    private async Task ExpireGroupWaitAsync(long parentId, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        Assert.NotEqual(0, await WaitTimeoutStaging.ForceGroupWaitDueAsync(Db, parentId, past, ct));
        await RewindRuntimeAsync(parentId, past, ct);
    }

    // Spends part of the group's budget without waiting for real time, by moving the stored deadline
    // alone. Deliberately not the shared expiry staging: an armed latch keeps its own due while the
    // remaining time shrinks under it, and that is the property two of these facts assert.
    private async Task SetGroupDeadlineAsync(long parentId, DateTime deadlineAtUtc, CancellationToken ct)
    {
        foreach (var slot in await ReadDeadlineSlotsAsync(parentId, ct))
        {
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET value = @p_value WHERE job_id = @p_id AND kind_code = 10 AND name = @p_name",
                ct,
                ("@p_value", Encoding.UTF8.GetBytes(deadlineAtUtc.Ticks.ToString(CultureInfo.InvariantCulture))),
                ("@p_id", parentId),
                ("@p_name", slot.Name)
            );
        }
    }

    private async Task RewindLatchAsync(long parentId, string latchName, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due WHERE job_id = @p_id AND kind_code = 50 AND name = @p_name",
            ct,
            ("@p_due", past),
            ("@p_id", parentId),
            ("@p_name", latchName)
        );
        await RewindRuntimeAsync(parentId, past, ct);
    }

    private Task RewindRuntimeAsync(long jobId, DateTime past, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", past),
            ("@p_id", jobId)
        );

    private Task SetJobStatusReadyAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );

    // Releases the member behind a `sys.child.{id}` latch by raising the signal its handler parks on.
    private async Task CompleteHeldChildAsync(string latchName, CancellationToken ct)
    {
        var childId = long.Parse(latchName["sys.child.".Length..], CultureInfo.InvariantCulture);
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(JobLookup.ById(childId), "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(childId, ct));
    }
}
