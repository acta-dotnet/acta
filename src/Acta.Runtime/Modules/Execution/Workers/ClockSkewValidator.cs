using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// The outcome of a worker-init clock-skew check.
/// </summary>
internal enum ClockSkewVerdict
{
    /// <summary>Measured skew is within the warn threshold.</summary>
    WithinTolerance,

    /// <summary>Measured skew exceeds the warn threshold but not the fail threshold; a warning was logged.</summary>
    Warned,

    /// <summary>Measured skew exceeds the fail threshold, but <c>AllowClockSkew</c> downgraded the failure to a warning.</summary>
    ExceededButAllowed,
}

/// <summary>
/// One clock-skew measurement: the estimated offset of the DB clock from the local clock, plus the
/// round-trip it cost to read the DB clock.
/// </summary>
/// <remarks>
/// The DB instant is sampled at some unknown point inside the round-trip, so the local clock at that
/// point is estimated as the midpoint of the two bracketing local reads. The estimate's uncertainty is
/// bounded by half the round-trip, which is why the validator keeps the sample with the smallest
/// round-trip across its repeated measurements.
/// </remarks>
internal readonly record struct ClockSkewSample(TimeSpan Skew, TimeSpan RoundTrip)
{
    /// <summary>
    /// Build a sample from a DB read bracketed by two local reads. <paramref name="dbNowUtc"/> minus the
    /// local midpoint is the signed skew (positive when the DB clock leads the local clock).
    /// </summary>
    public static ClockSkewSample From(DateTime localBeforeUtc, DateTime dbNowUtc, DateTime localAfterUtc)
    {
        var roundTrip = localAfterUtc - localBeforeUtc;
        var localMidpoint = localBeforeUtc + TimeSpan.FromTicks(roundTrip.Ticks / 2);
        return new ClockSkewSample(dbNowUtc - localMidpoint, roundTrip);
    }

    /// <summary>
    /// The most accurate sample: the one with the smallest round-trip, whose skew estimate has the
    /// tightest uncertainty bound.
    /// </summary>
    public static ClockSkewSample Best(IReadOnlyList<ClockSkewSample> samples)
    {
        var best = samples[0];
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].RoundTrip < best.RoundTrip)
            {
                best = samples[i];
            }
        }

        return best;
    }
}

/// <summary>
/// Worker-init guard against host/DB clock skew. Reads the DB server clock a few times, keeps the
/// tightest measurement, and warns or fails when the offset from the local clock crosses the
/// configured thresholds. A drifted host clock silently corrupts lease-expiry and schedule math, so
/// catching it at startup turns a class of hard-to-diagnose distributed bugs into one clear failure.
/// </summary>
/// <remarks>
/// The DB clock is read through the supplied delegate (the real <c>GetUtcNow</c> op in production), not
/// the schedule <c>IActaClock</c>; tests substitute a deterministic schedule clock, and conflating the
/// two would make every fake-clock test trip the skew guard. The local clock comes from an injected
/// <see cref="TimeProvider"/> for the same testability reason.
/// </remarks>
internal sealed class ClockSkewValidator(
    Func<CancellationToken, Task<DateTime>> readDbNowUtc,
    TimeProvider localClock,
    TimeSpan warnThreshold,
    TimeSpan failThreshold,
    bool allowOverride,
    ILogger log,
    int sampleCount = ClockSkewValidator.DefaultSampleCount
)
{
    private const int DefaultSampleCount = 3;

    /// <summary>Skew above which worker init logs a warning.</summary>
    public static readonly TimeSpan DefaultWarnThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Skew above which worker init fails, unless <c>AllowClockSkew</c> is set.</summary>
    public static readonly TimeSpan DefaultFailThreshold = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<DateTime>> _readDbNowUtc = readDbNowUtc;
    private readonly TimeProvider _localClock = localClock;
    private readonly TimeSpan _warnThreshold = warnThreshold;
    private readonly TimeSpan _failThreshold = failThreshold;
    private readonly bool _allowOverride = allowOverride;
    private readonly int _sampleCount = sampleCount < 1 ? 1 : sampleCount;
    private readonly ILogger _log = log;

    /// <summary>
    /// Measure skew and apply the threshold policy. Throws <see cref="InvalidOperationException"/> when
    /// the skew exceeds the fail threshold and <c>AllowClockSkew</c> is not set; otherwise returns the
    /// verdict (logging a warning in the warn / allowed-fail bands).
    /// </summary>
    public async Task<ClockSkewVerdict> ValidateAsync(string namespaceName, CancellationToken ct)
    {
        var sample = await MeasureBestAsync(ct);
        var magnitude = sample.Skew.Duration();
        var skewSeconds = sample.Skew.TotalSeconds;

        if (magnitude <= _warnThreshold)
        {
            _log.LogDebug(
                "Acta worker clock skew {DurationMs}ms is within tolerance for namespace {Namespace} ({Detail}).",
                (long)sample.Skew.TotalMilliseconds,
                namespaceName,
                $"round-trip {sample.RoundTrip.TotalMilliseconds:F0}ms"
            );
            return ClockSkewVerdict.WithinTolerance;
        }

        if (magnitude <= _failThreshold)
        {
            _log.LogWarning(
                "Acta worker clock skew {DurationMs}ms exceeds the warn threshold for namespace {Namespace} ({Detail}); lease and schedule timing may drift.",
                (long)sample.Skew.TotalMilliseconds,
                namespaceName,
                $"warn threshold {_warnThreshold.TotalMilliseconds:F0}ms, round-trip {sample.RoundTrip.TotalMilliseconds:F0}ms"
            );
            return ClockSkewVerdict.Warned;
        }

        if (_allowOverride)
        {
            _log.LogWarning(
                "Acta worker clock skew {DurationMs}ms exceeds the fail threshold for namespace {Namespace} ({Detail}), but AllowClockSkew is set; proceeding.",
                (long)sample.Skew.TotalMilliseconds,
                namespaceName,
                $"fail threshold {_failThreshold.TotalMilliseconds:F0}ms"
            );
            return ClockSkewVerdict.ExceededButAllowed;
        }

        throw new InvalidOperationException(
            $"Worker clock skew {skewSeconds:F3}s (DB clock vs local clock) exceeds the {_failThreshold.TotalSeconds:F0}s fail "
                + $"threshold for namespace '{namespaceName}'. Synchronize the host clock (e.g. NTP) so it matches the database "
                + "server, or set JobsOptions.AllowClockSkew = true to override."
        );
    }

    private async Task<ClockSkewSample> MeasureBestAsync(CancellationToken ct)
    {
        var samples = new List<ClockSkewSample>(_sampleCount);
        for (var i = 0; i < _sampleCount; i++)
        {
            var before = _localClock.GetUtcNow().UtcDateTime;
            var dbNow = await _readDbNowUtc(ct);
            var after = _localClock.GetUtcNow().UtcDateTime;
            samples.Add(ClockSkewSample.From(before, dbNow, after));
        }

        return ClockSkewSample.Best(samples);
    }
}
