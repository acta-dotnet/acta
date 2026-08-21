using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Call-site contract for the bounded wait overloads: the timeout is validated before any substrate
/// call, and the new core sink is additive, so a JobContext subclass that only implements the
/// unbounded sink keeps working (and simply never sees a timeout).
/// </summary>
public sealed class JobContextWaitTimeoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_timeout_is_rejected_before_any_store_call(int seconds)
    {
        // RecordingJobContext's wait sink only understands child-latch names, so reaching it at all
        // would throw something other than ArgumentOutOfRangeException: this asserts the order too.
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var timeout = TimeSpan.FromSeconds(seconds);

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync<string>("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync<string>("go", timeout, ct));
        Assert.Empty(ctx.Events);

        // Both non-positive cases say the same thing about the same parameter. Delegating the negative
        // one to DurationSyntax would have named it a "Delay", which is not what the caller passed.
        Assert.Equal("timeout", thrown.ParamName);
        Assert.Contains("Wait timeout must be positive.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subclass_implementing_only_the_unbounded_sink_still_serves_a_bounded_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();

        var result = await ctx.TryWaitSignalAsync("go", TimeSpan.FromMinutes(5), ct);

        Assert.True(result.Received);
        Assert.False(result.TimedOut);
        Assert.Equal(["wait:go"], ctx.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_bounded_child_wait_validates_its_arguments_before_any_store_call(int seconds)
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new { }, ct: ct);

        var badTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ctx.TryWaitChildAsync(child.JobId, TimeSpan.FromSeconds(seconds), ct)
        );
        var badId = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitChildAsync(0, TimeSpan.FromMinutes(5), ct));

        // Each throw names the argument the caller actually got wrong, not whichever guard ran first.
        Assert.Equal("timeout", badTimeout.ParamName);
        Assert.Equal("childJobId", badId.ParamName);
        Assert.Equal(["start:only"], ctx.Events);
    }

    [Fact]
    public async Task A_child_landing_before_the_deadline_returns_its_outcome_and_cancels_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new { }, ct: ct);

        var result = await ctx.TryWaitChildAsync(child.JobId, TimeSpan.FromMinutes(5), ct);

        Assert.True(result.Completed);
        Assert.False(result.TimedOut);
        Assert.Equal(child.JobId, result.ChildJobId);
        Assert.Equal(JobStatusCode.Succeeded, result.Outcome!.Status);
        Assert.Equal(["start:only", "wait:only"], ctx.Events);
    }

    [Fact]
    public async Task A_timed_out_child_wait_cancels_the_child_before_it_returns()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new TimingOutChildContext();

        var result = await ctx.TryWaitChildAsync(7, TimeSpan.FromMinutes(5), ct);

        // The ordering is the point: the handler must never regain control while the subtree it gave
        // up on is still live.
        Assert.True(result.TimedOut);
        Assert.False(result.Completed);
        Assert.Equal(7, result.ChildJobId);
        Assert.Null(result.Outcome);
        Assert.Equal(["wait:sys.child.7", "cancel:7"], ctx.Events);
    }

    [Fact]
    public async Task A_bounded_group_wait_validates_everything_before_any_store_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new { }, ct: ct);

        var badTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ctx.TryWaitChildrenAsync([child.JobId], TimeSpan.Zero, ct)
        );
        var badId = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ctx.TryWaitChildrenAsync([child.JobId, 0], TimeSpan.FromMinutes(5), ct)
        );
        await Assert.ThrowsAsync<ArgumentNullException>(() => ctx.TryWaitChildrenAsync(null!, TimeSpan.FromMinutes(5), ct));

        // A bad id anywhere in the list stops the call before the deadline slot is written, so a
        // rejected group never leaves a half-armed one behind.
        Assert.Equal("timeout", badTimeout.ParamName);
        Assert.Equal("childJobIds", badId.ParamName);
        Assert.Equal(["start:only"], ctx.Events);
    }

    [Fact]
    public async Task A_bounded_wrapper_rejects_its_timeout_before_it_starts_a_child()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var zero = TimeSpan.Zero;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.ExecuteChildAsync("only", new { }, zero, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.ExecuteChildAsync<object, string>("only", new { }, zero, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.ParallelAsync("grp", b => b.Child("a", new { }), zero, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.MapAsync("grp", (int[])[1], i => i, i => new { }, zero, ct));

        // Every wrapper starts its children before it waits on any of them, so a rejected timeout must
        // not leave enqueued work that nothing is going to join on.
        Assert.Empty(ctx.Events);
    }

    [Fact]
    public async Task An_empty_bounded_group_wait_resolves_without_touching_the_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();

        var result = await ctx.TryWaitChildrenAsync([], TimeSpan.FromMinutes(5), ct);

        Assert.Empty(result.Children);
        Assert.False(result.TimedOut);
        Assert.True(result.Succeeded);
        Assert.Empty(ctx.Events);
    }

    [Fact]
    public async Task A_timed_out_group_member_reports_the_timeout_and_is_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new TimingOutChildContext();

        var result = await ctx.TryWaitChildrenAsync([7, 9], TimeSpan.FromMinutes(5), ct);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal([7, 9], result.Children.Select(c => c.ChildJobId));
        Assert.All(result.Children, c => Assert.True(c.TimedOut));
        // Each member is cancelled through the same single-child path, in group order, and the group
        // deadline is read once for the whole call rather than once per member.
        Assert.Equal(
            ["deadline:sys.wait-group.09f9b3b89bd0ea13", "wait:sys.child.7", "cancel:7", "wait:sys.child.9", "cancel:9"],
            ctx.Events
        );

        var thrown = Assert.Throws<ChildGroupException>(result.ThrowIfAnyFailed);
        Assert.Contains("7:TimedOut", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_reached_later_in_the_walk_counts_down_from_where_the_walk_got_to()
    {
        var passNowUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var deadlineAtUtc = passNowUtc.AddSeconds(100);

        // The first member is measured at the pass's own clock reading; a member reached after the walk
        // has spent 30 seconds on earlier members is measured from where the walk got to, so the walk
        // comes out of the group's budget instead of being added to its deadline as overshoot.
        Assert.Equal(TimeSpan.FromSeconds(100), JobContext.RemainingWait(deadlineAtUtc, passNowUtc, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(70), JobContext.RemainingWait(deadlineAtUtc, passNowUtc, TimeSpan.FromSeconds(30)));

        // However late in the walk a member arms, its due lands at or before the deadline: the remaining
        // is what is left at that moment, floored to whole seconds, so elapsed + remaining never exceeds
        // the budget. The one exception is the positive-bound floor, which the next fact owns.
        foreach (var elapsed in (TimeSpan[])[TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60.7), TimeSpan.FromSeconds(98)])
        {
            var armedAtUtc = passNowUtc + elapsed;
            Assert.True(
                armedAtUtc + JobContext.RemainingWait(deadlineAtUtc, passNowUtc, elapsed) <= deadlineAtUtc,
                $"a member armed {elapsed} into the walk outlived the deadline."
            );
        }
    }

    [Fact]
    public void A_walk_that_outlasts_the_deadline_still_arms_a_positive_bound()
    {
        var passNowUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var deadlineAtUtc = passNowUtc.AddSeconds(10);

        // Sub-second and already-passed both floor to one second: a wait must carry a positive bound,
        // and the arbiter resolves the overshoot on the next re-entry rather than at the arm.
        Assert.Equal(TimeSpan.FromSeconds(1), JobContext.RemainingWait(deadlineAtUtc, passNowUtc, TimeSpan.FromSeconds(9.5)));
        Assert.Equal(TimeSpan.FromSeconds(1), JobContext.RemainingWait(deadlineAtUtc, passNowUtc, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(1), JobContext.RemainingWait(deadlineAtUtc, passNowUtc, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task The_group_deadline_slot_is_named_by_the_children_not_by_their_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var ascending = new TimingOutChildContext();
        var descending = new TimingOutChildContext();

        await ascending.TryWaitChildrenAsync([7, 9], TimeSpan.FromMinutes(5), ct);
        var result = await descending.TryWaitChildrenAsync([9, 7], TimeSpan.FromMinutes(5), ct);

        // The slot names the SET of children, so a handler that lists the same ids in a different order
        // between replays cannot mint a second deadline and restart the group's budget.
        Assert.Equal("deadline:sys.wait-group.09f9b3b89bd0ea13", ascending.Events[0]);
        Assert.Equal(ascending.Events[0], descending.Events[0]);

        // Caller order still decides the outcome order; only the slot name is canonical.
        Assert.Equal([9, 7], result.Children.Select(c => c.ChildJobId));
    }

    /// <summary>
    /// A context whose bounded wait always expires, so the resolution order the runtime relies on can
    /// be pinned without a database.
    /// </summary>
    private sealed class TimingOutChildContext : RecordingJobContext
    {
        protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(
            string name,
            int? timeoutSeconds,
            bool resumeOnTimeout,
            CancellationToken ct
        )
        {
            Events.Add($"wait:{name}");
            return Task.FromResult(new SignalWaitOutcome(0, null, TimedOut: true));
        }

        protected override Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
        {
            Events.Add($"cancel:{childJobId}");
            return Task.CompletedTask;
        }

        protected override Task<WaitDeadline> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            Events.Add($"deadline:{name}");
            return base.GetOrSetWaitDeadlineCoreAsync(name, timeout, ct);
        }
    }
}
