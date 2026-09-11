namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it, so a loop that sleeps out
/// a production interval can be driven through a tick without the test waiting that interval. Supports
/// the two timers the runtime's loops build on: <c>Task.Delay(delay, provider, ct)</c> and
/// <c>PeriodicTimer(period, provider)</c>.
/// </summary>
/// <remarks>
/// Callbacks fire outside the lock, because a fired timer's continuation arms the next one on the
/// calling thread and would otherwise re-enter it. <see cref="FirstTimerArmed"/> exists because a test
/// that advances before the loop under test has armed its timer advances past nothing.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Completes once anything has armed a timer on this provider.</summary>
    public Task FirstTimerArmed => _armed.Task;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer the move made due, in one pass.</summary>
    public void Advance(TimeSpan delta)
    {
        FakeTimer[] due;
        lock (_gate)
        {
            _now += delta;
            due = [.. _timers.Where(t => t.DueAtUtc is { } at && at <= _now)];
            foreach (var timer in due)
            {
                timer.DueAtUtc = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Arm(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            timer.DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }

        _armed.TrySetResult();
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAtUtc { get; set; }

        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Arm(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
