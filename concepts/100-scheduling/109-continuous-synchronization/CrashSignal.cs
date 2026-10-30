namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// Where the lab injects failure: called after every batch, once the orders and the watermark are durable, so a crash
/// lands at the worst moment, before Acta's attempt completes. The lab's process kills itself when armed; a test
/// replaces this with something it can observe.
/// </summary>
public abstract class CrashSignal
{
    public abstract Task AfterBatchAsync(long committedChangeId, CancellationToken ct);
}

/// <summary>Never fires.</summary>
public sealed class NoCrashSignal : CrashSignal
{
    public override Task AfterBatchAsync(long committedChangeId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Armed by a marker file the lab's crash command writes. The marker goes before the process does, so the restarted
/// worker recovers the firing instead of crashing on the same batch again.
/// </summary>
public sealed class ProcessCrashSignal(string markerPath) : CrashSignal
{
    public override Task AfterBatchAsync(long committedChangeId, CancellationToken ct)
    {
        if (!File.Exists(markerPath))
        {
            return Task.CompletedTask;
        }

        File.Delete(markerPath);
        Console.WriteLine(
            $"[sync-orders] crash armed: the batch through change {committedChangeId} is committed; the process disappears now."
        );
        Console.Out.Flush();
        Environment.FailFast("Intentional worker crash for the continuous-synchronization lab.");
        return Task.CompletedTask;
    }
}
