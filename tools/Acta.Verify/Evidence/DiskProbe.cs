using System.Diagnostics;

namespace Acta.Verify;

/// <summary>
/// The drive's flush latency at one moment, read just before and just after each load trial, so a result can be weighed
/// against the slow state <see cref="DiskCounters"/> describes, which the counters of an idle drive cannot show. It
/// overwrites one 8 KB block and forces it to the device 64 times in the trial's directory: the drive of the SQLite files,
/// and of the server containers when Docker keeps its disk there.
/// </summary>
public static class DiskProbe
{
    private const int Flushes = 64;

    /// <summary>The median of the flushes, in milliseconds.</summary>
    public static double FlushMilliseconds(string directory)
    {
        var path = Path.Combine(directory, "disk-probe.tmp");
        var block = new byte[8192];
        var times = new double[Flushes];
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0);
            for (var i = 0; i < Flushes; i++)
            {
                var start = Stopwatch.GetTimestamp();
                file.Position = 0;
                file.Write(block);
                file.Flush(flushToDisk: true);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }
        finally
        {
            File.Delete(path);
        }
        Array.Sort(times);
        return times[Flushes / 2];
    }
}
