using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A worker's loops, and a host's runtimes, stand or fall together: one that faults cancels its siblings and its fault
/// reaches the awaiting host, so a worker never keeps heartbeating with a dead claim loop. A stop the host asked for is
/// not a fault.
/// </summary>
public sealed class StopOnFaultTests
{
    [Fact]
    public async Task A_loop_that_faults_stops_its_siblings_and_surfaces_the_fault()
    {
        var stop = new CancellationTokenSource();
        var sibling = WorkerRuntime.StopOnFaultAsync(Task.Delay(Timeout.Infinite, stop.Token), stop);
        var faulting = WorkerRuntime.StopOnFaultAsync(FailAsync(), stop);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(sibling, faulting));

        Assert.Equal("claim loop", thrown.Message);
        Assert.True(stop.IsCancellationRequested);
        Assert.True(sibling.IsCanceled);
    }

    [Fact]
    public async Task A_stop_the_host_asked_for_ends_every_loop_without_a_fault()
    {
        var host = new CancellationTokenSource();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
        var loops = new[]
        {
            WorkerRuntime.StopOnFaultAsync(Task.Delay(Timeout.Infinite, stop.Token), stop),
            WorkerRuntime.StopOnFaultAsync(Task.Delay(Timeout.Infinite, stop.Token), stop),
        };

        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(loops));
        Assert.All(loops, loop => Assert.True(loop.IsCanceled));
    }

    private static async Task FailAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("claim loop");
    }
}
