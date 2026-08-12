namespace Anvil;

/// <summary>
/// A server-side rolling window over the count snapshots, so the front end stays dumb and the rates stay
/// consistent. Records (utc, done, ready, executing) samples ~1 Hz. Samples closer than the min interval
/// are dropped, so poll frequency never inflates the rate. One per dashboard session; the state reader
/// records on each read and reads the snapshot back.
/// </summary>
public sealed class RateTelemetry
{
    /// <summary>Max samples retained (~2 minutes at 1 Hz), which is the sparkline's span.</summary>
    public const int Capacity = 120;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    // The headline rate is measured over this trailing slice, not over the whole retained series. Slope
    // across the full two minutes smears a burst flat and takes two minutes to catch up with a change,
    // which reads as lag on a screen showing live throughput. The series keeps its full span for the
    // sparkline, where the long view is what you want.
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Queue<RatePoint> _points = new(Capacity);

    public void Record(DateTime utcNow, long done, long ready, long executing)
    {
        lock (_gate)
        {
            if (_points.Count > 0 && utcNow - _points.Last().TimeUtc < MinInterval)
            {
                return;
            }

            if (_points.Count == Capacity)
            {
                _points.Dequeue();
            }

            _points.Enqueue(new RatePoint(utcNow, done, ready, executing));
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            var series = _points.ToArray();
            var donePerSecond = 0.0;
            if (series.Length >= 2)
            {
                var last = series[^1];
                // Walk back to the newest sample at or before the cutoff, so the window is a full
                // RateWindow once the history is long enough and the whole history before that.
                var cutoff = last.TimeUtc - RateWindow;
                var firstIndex = 0;
                for (var i = series.Length - 2; i >= 0; i--)
                {
                    if (series[i].TimeUtc <= cutoff)
                    {
                        firstIndex = i;
                        break;
                    }
                }

                var first = series[firstIndex];
                var seconds = (last.TimeUtc - first.TimeUtc).TotalSeconds;
                if (seconds > 0)
                {
                    donePerSecond = Math.Max(0, last.Done - first.Done) / seconds;
                }
            }

            return new TelemetrySnapshot(donePerSecond, series);
        }
    }
}

/// <summary>One telemetry sample: the count snapshot at an instant.</summary>
public sealed record RatePoint(DateTime TimeUtc, long Done, long Ready, long Executing);

/// <summary>Derived telemetry for the scopes: current throughput plus the raw series for the sparklines.</summary>
public sealed record TelemetrySnapshot(double DonePerSecond, IReadOnlyList<RatePoint> Series);
