namespace Acta.Verify;

/// <summary>Drops every wake signal and prints one line per signal, which the controller counts.</summary>
internal sealed class SuppressedWakeup : IWorkerWakeup
{
    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        Console.WriteLine("ACTA_VERIFY_WAKEUP_DROPPED");
        return ValueTask.CompletedTask;
    }

    public async ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(timeout, ct);
        return WorkerWakeupWaitStatus.TimedOut;
    }
}
