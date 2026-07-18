namespace Anvil;

/// <summary>
/// Tracks the current (or most recent) seed run so the dashboard can show enqueued/target while a large
/// load is still being written, instead of a frozen button. One per dashboard session; a new seed run
/// calls <see cref="Begin"/> to reset. Thread-safe: the seeder advances it from a background task while
/// the state endpoint reads <see cref="Snapshot"/>.
/// </summary>
public sealed class SeedProgress
{
    private readonly object _gate = new();
    private bool _active;
    private long _target;
    private long _processed;
    private long _inserted;
    private long _deduplicated;
    private DateTime? _startedAtUtc;
    private DateTime? _finishedAtUtc;
    private string? _error;

    public void Begin(long target)
    {
        lock (_gate)
        {
            _target = target;
            _processed = 0;
            _inserted = 0;
            _deduplicated = 0;
            _startedAtUtc = DateTime.UtcNow;
            _finishedAtUtc = null;
            _error = null;
            _active = true;
        }
    }

    public void Advance(long inserted, long deduplicated)
    {
        lock (_gate)
        {
            _inserted += inserted;
            _deduplicated += deduplicated;
            _processed += inserted + deduplicated;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _active = false;
            _finishedAtUtc = DateTime.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _error = error;
            _active = false;
            _finishedAtUtc = DateTime.UtcNow;
        }
    }

    public SeedProgressSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _finishedAtUtc ?? DateTime.UtcNow;
            var elapsed = _startedAtUtc is { } started ? Math.Max(0, (now - started).TotalSeconds) : 0;
            return new SeedProgressSnapshot(
                _active,
                _target,
                _processed,
                _inserted,
                _deduplicated,
                elapsed > 0 ? _processed / elapsed : 0,
                _startedAtUtc,
                _finishedAtUtc,
                _error
            );
        }
    }
}

/// <summary>An immutable view of seeding progress for the dashboard: how far a seed run has gotten.</summary>
public sealed record SeedProgressSnapshot(
    bool Active,
    long Target,
    long Processed,
    long Inserted,
    long Deduplicated,
    double PerSecond,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? Error
);
