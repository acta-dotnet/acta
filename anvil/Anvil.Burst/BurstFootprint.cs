using System.Diagnostics;

namespace Anvil.Burst;

/// <summary>
/// Samples this process's footprint across a phase: peak working set, peak thread count, and the bytes
/// allocated over the window.
/// </summary>
/// <remarks>
/// <para>
/// The 100K claim is that projection is bounded in memory and creates no unbounded task fan-out, and both
/// halves are properties of the process the projector runs in - which is this one, by construction (see
/// <see cref="BurstHost"/>). <see cref="Process.PeakWorkingSet64"/> would answer a different question:
/// it is the peak since the process started, so it carries the seeding phase's footprint into the
/// projection phase's number. Sampling scopes the peak to the window the caller cares about.
/// </para>
/// <para>
/// Thread count stands in for "no unbounded task creation". The projector awaits its batches in sequence
/// and starts nothing, so the thread pool has nothing to grow for; a drain that walked the pool up is the
/// observable shape of the failure this bound exists to prevent.
/// </para>
/// </remarks>
internal sealed class BurstFootprint : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sampler;
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes();
    private long _peakWorkingSetBytes;
    private int _peakThreads;

    private BurstFootprint(TimeSpan interval)
    {
        _sampler = Task.Run(async () =>
        {
            using var process = Process.GetCurrentProcess();
            while (!_stopping.IsCancellationRequested)
            {
                process.Refresh();
                _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, process.WorkingSet64);
                _peakThreads = Math.Max(_peakThreads, process.Threads.Count);
                try
                {
                    await Task.Delay(interval, _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>Peak resident set observed over the window, in megabytes.</summary>
    public double PeakWorkingSetMb => _peakWorkingSetBytes / (1024.0 * 1024.0);

    /// <summary>Peak OS thread count observed over the window.</summary>
    public int PeakThreads => _peakThreads;

    /// <summary>Managed bytes allocated since the window opened, in megabytes.</summary>
    public double AllocatedMb => (GC.GetTotalAllocatedBytes() - _allocatedAtStart) / (1024.0 * 1024.0);

    public static BurstFootprint Start() => new(TimeSpan.FromMilliseconds(250));

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _sampler.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The sampler exits through its own delay; either shape ends the window.
        }
        _stopping.Dispose();
    }
}
