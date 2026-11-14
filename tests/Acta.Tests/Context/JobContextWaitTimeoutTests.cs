using System.Globalization;
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
    public async Task A_timeout_of_zero_or_less_waits_the_one_second_minimum(int seconds)
    {
        // A remaining time computed against a deadline goes negative on the replay after it. The wait has to reach the
        // store, which answers from the expiration the first pass stored, rather than throw before it.
        var ct = TestContext.Current.CancellationToken;
        var ctx = new BoundRecordingContext();
        var timeout = TimeSpan.FromSeconds(seconds);

        await ctx.WaitSignalAsync("go", timeout, ct);
        await ctx.WaitSignalAsync<string>("go", timeout, ct);
        await ctx.TryWaitSignalAsync("go", timeout, ct);
        await ctx.TryWaitSignalAsync<string>("go", timeout, ct);
        await ctx.WaitChildAsync(await ctx.EnqueueChildAsync("only", new { }, ct: ct), timeout, ct);

        Assert.Equal<int?>([1, 1, 1, 1, 1], ctx.Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_sleep_of_zero_or_less_reaches_the_store_with_a_zero_delay(int seconds)
    {
        // A delay computed against a deadline goes negative on the replay after it. The sleep still reaches the store,
        // which consumes the timer the first pass armed, so that timer does not stay pending and refuse a later sleep.
        var ct = TestContext.Current.CancellationToken;
        var ctx = new SleepRecordingContext();

        await ctx.SleepAsync("cool-down", TimeSpan.FromSeconds(seconds), ct: ct);

        Assert.Equal<TimeSpan?>([TimeSpan.Zero], ctx.Delays);
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

    [Fact]
    public async Task A_bounded_child_wait_validates_its_arguments_before_any_store_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.EnqueueChildAsync("only", new { }, ct: ct);

        var badTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitChildAsync(child, TimeSpan.MaxValue, ct));
        var badId = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitChildAsync(Child(0), TimeSpan.FromMinutes(5), ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ctx.WaitChildAsync(null!, TimeSpan.FromMinutes(5), ct));

        // Each throw names the argument the caller actually got wrong, not whichever guard ran first.
        Assert.Equal("timeout", badTimeout.ParamName);
        Assert.Equal("child", badId.ParamName);
        Assert.Equal(["start:only"], ctx.Events);
    }

    [Fact]
    public async Task A_child_landing_before_the_deadline_returns_its_outcome_and_cancels_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.EnqueueChildAsync("only", new { }, ct: ct);

        var result = await ctx.WaitChildAsync(child, TimeSpan.FromMinutes(5), ct);

        Assert.False(result.TimedOut);
        Assert.Equal((child.JobId, child.JobRef), (result.JobId, result.JobRef));
        Assert.Equal(JobStatusCode.Succeeded, result.Status);
        Assert.Equal(["start:only", "wait:only"], ctx.Events);
    }

    [Fact]
    public async Task A_timed_out_child_wait_cancels_the_child_before_it_returns()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new TimingOutChildContext();

        var result = await ctx.WaitChildAsync(Child(7), TimeSpan.FromMinutes(5), ct);

        // The ordering is the point: the handler must never regain control while the subtree it gave
        // up on is still live. The outcome reports Cancelled, the status Acta drove the child to.
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal((7L, JobStatusCode.Cancelled), (result.JobId, result.Status));
        Assert.Equal(["wait:sys.child.7", "cancel:7"], ctx.Events);
    }

    [Fact]
    public async Task A_bounded_wrapper_rejects_its_timeout_before_it_starts_a_child()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var tooLong = TimeSpan.MaxValue;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.CallChildAsync("only", new { }, tooLong, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.MapAsync("grp", (int[])[1], i => i, i => new { }, tooLong, ct));

        // Every wrapper starts its children before it waits on any of them, so a rejected timeout must
        // not leave enqueued work that nothing is going to join on.
        Assert.Empty(ctx.Events);
    }

    [Fact]
    public async Task An_empty_bounded_group_wait_resolves_without_touching_the_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();

        var result = await ctx.WaitChildrenAsync([], TimeSpan.FromMinutes(5), ct);

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

        var seven = Child(7);
        var result = await ctx.WaitChildrenAsync([seven, Child(9)], TimeSpan.FromMinutes(5), ct);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal([7, 9], result.Children.Select(c => c.JobId));
        Assert.All(result.Children, c => Assert.True(c.TimedOut));
        // Each member is cancelled through the same single-child path, in group order, and the group
        // deadline is read once for the whole call rather than once per member.
        Assert.Equal(
            ["deadline:sys.wait-group.09f9b3b89bd0ea13", "wait:sys.child.7", "cancel:7", "wait:sys.child.9", "cancel:9"],
            ctx.Events
        );

        var thrown = Assert.Throws<ChildGroupException>(result.ThrowIfAnyFailed);
        Assert.Contains($"{seven.JobRef}:TimedOut", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_group_member_waits_toward_the_stored_deadline_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new FixedDeadlineContext();

        await ctx.WaitChildrenAsync([Child(7), Child(9), Child(11)], TimeSpan.FromMinutes(5), ct);

        // Each member is bounded by the one stored instant rather than by a length measured when the walk
        // reaches it, so however long the walk takes, no member's expiry lands past the group's deadline.
        var due = FixedDeadlineContext.GroupDeadlineUtc.ToString("O", CultureInfo.InvariantCulture);
        Assert.Equal([$"until:sys.child.7@{due}", $"until:sys.child.9@{due}", $"until:sys.child.11@{due}"], ctx.Events);
    }

    [Theory]
    [InlineData(-5000, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1001, 2)]
    [InlineData(5000, 5)]
    public async Task A_group_bound_reaches_its_deadline_in_whole_seconds_rounded_up(int milliseconds, int seconds)
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new FixedDeadlineContext();

        await ctx.WaitChildrenAsync([Child(7), Child(9)], TimeSpan.FromMilliseconds(milliseconds), ct);

        // A single child's bound rounds up to whole seconds, and a group's is documented the same: a millisecond
        // passed through as written would be spent by the round trip that stores it.
        Assert.Equal([TimeSpan.FromSeconds(seconds)], ctx.Bounds);
    }

    [Fact]
    public void The_group_deadline_name_is_a_property_of_the_child_set()
    {
        var canonical = JobContext.GroupDeadlineName([7, 9]);

        // Reordering and duplicate listings are the same group and must share one budget checkpoint; a
        // genuinely different set is a different group.
        Assert.Equal(canonical, JobContext.GroupDeadlineName([9, 7]));
        Assert.Equal(canonical, JobContext.GroupDeadlineName([7, 7, 9]));
        Assert.NotEqual(canonical, JobContext.GroupDeadlineName([7, 9, 11]));
    }

    [Fact]
    public async Task The_group_deadline_checkpoint_is_named_by_the_children_not_by_their_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var ascending = new TimingOutChildContext();
        var descending = new TimingOutChildContext();

        await ascending.WaitChildrenAsync([Child(7), Child(9)], TimeSpan.FromMinutes(5), ct);
        var result = await descending.WaitChildrenAsync([Child(9), Child(7)], TimeSpan.FromMinutes(5), ct);

        // The checkpoint names the SET of children, so a handler that lists the same ids in a different order
        // between replays cannot mint a second deadline and restart the group's budget.
        Assert.Equal("deadline:sys.wait-group.09f9b3b89bd0ea13", ascending.Events[0]);
        Assert.Equal(ascending.Events[0], descending.Events[0]);

        // Caller order still decides the outcome order; only the checkpoint name is canonical.
        Assert.Equal([9, 7], result.Children.Select(c => c.JobId));
    }

    /// <summary>What a start answers for <paramref name="jobId"/>; the scripted contexts answer a wait on any id.</summary>
    private static EnqueueJobResponse Child(long jobId) => new(jobId, JobRef.New(), EnqueueJobAction.Inserted);

    /// <summary>
    /// A context whose bounded wait always expires, so the resolution order the runtime relies on can
    /// be pinned without a database.
    /// </summary>
    private sealed class TimingOutChildContext : RecordingJobContext
    {
        private protected override Task<SignalCheckpointState> WaitSignalCoreAsync(
            string name,
            int? timeoutSeconds,
            bool resumeOnTimeout,
            CancellationToken ct
        )
        {
            Events.Add($"wait:{name}");
            return Task.FromResult(new SignalCheckpointState(0, null, TimedOut: true));
        }

        private protected override Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
        {
            Events.Add($"cancel:{childJobId}");
            return Task.CompletedTask;
        }

        private protected override Task<DateTime> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            Events.Add($"deadline:{name}");
            return base.GetOrSetWaitDeadlineCoreAsync(name, timeout, ct);
        }
    }

    /// <summary>A context that records the bound every bounded wait hands the store.</summary>
    private sealed class BoundRecordingContext : RecordingJobContext
    {
        public List<int?> Bounds { get; } = [];

        private protected override Task<SignalCheckpointState> WaitSignalCoreAsync(
            string name,
            int? timeoutSeconds,
            bool resumeOnTimeout,
            CancellationToken ct
        )
        {
            Bounds.Add(timeoutSeconds);
            return base.WaitSignalCoreAsync(name, timeoutSeconds, resumeOnTimeout, ct);
        }
    }

    /// <summary>A context that records the delay every sleep hands the store, which answers continue.</summary>
    private sealed class SleepRecordingContext : RecordingJobContext
    {
        public List<TimeSpan?> Delays { get; } = [];

        private protected override Task SleepCoreAsync(
            string name,
            TimeSpan? delay,
            DateTime? resumeAtUtc,
            string? reason,
            CancellationToken ct
        )
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A context whose group deadline is one known instant, that records the bound it was asked to store, and whose
    /// instant-bounded wait records what it was given.
    /// </summary>
    private sealed class FixedDeadlineContext : RecordingJobContext
    {
        public static readonly DateTime GroupDeadlineUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        public List<TimeSpan> Bounds { get; } = [];

        private protected override Task<DateTime> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            Bounds.Add(timeout);
            return Task.FromResult(GroupDeadlineUtc);
        }

        private protected override Task<SignalCheckpointState> WaitSignalUntilCoreAsync(
            string name,
            DateTime dueAtUtc,
            CancellationToken ct
        )
        {
            Events.Add($"until:{name}@{dueAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            return Task.FromResult(new SignalCheckpointState(0, null, TimedOut: true));
        }
    }
}
