using System.Diagnostics;
using System.Globalization;

namespace Acta.Bench;

// Internal worker and checker processes must not outlive their owning controller.
internal sealed class ParentLifetime : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watch;

    private ParentLifetime(int pid, long started)
    {
        _watch = WatchAsync(pid, started);
    }

    public static ParentLifetime? ForInternalRole(string[] args)
    {
        if (args is not (["--worker"] or ["--oracle"]))
        {
            return null;
        }
        if (
            !int.TryParse(Environment.GetEnvironmentVariable("ACTA_BENCH_PARENT_PID"), CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(Environment.GetEnvironmentVariable("ACTA_BENCH_PARENT_START"), CultureInfo.InvariantCulture, out var started)
        )
        {
            throw new ArgumentException("Internal roles require an owning controller.");
        }
        return new(pid, started);
    }

    private async Task WatchAsync(int pid, long started)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var alive = false;
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    alive = !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == started;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                if (!alive)
                {
                    Environment.Exit(130);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _watch.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
