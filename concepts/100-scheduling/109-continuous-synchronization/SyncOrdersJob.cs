namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// One permanent job keeps the local orders current. The schedule fires it every five seconds; a firing applies at
/// most one batch of <see cref="BatchSize"/> changes and returns, so the slot sits Idle between firings and no thread
/// loops. The watermark, the id of the last shop change applied, is the job's own durable variable: Acta carries it
/// across firings, retries, and workers, and the handler reads it at the top of every execution, so an attempt that
/// dies after a batch is continued at the next batch by whichever worker runs the job next.
/// </summary>
public sealed class SyncOrdersJob(ShopFeed shop, OrderStore store, CrashSignal crash)
{
    public const int BatchSize = 40;

    [Job("sync-orders", MaxAttempts = 3, Backoff = "2s")]
    [Schedule("every-5s", "PT5S")]
    public async Task Sync(JobContext ctx, CancellationToken ct)
    {
        var watermark = await ctx.GetVariableOrDefaultAsync("watermark", 0L, ct);
        var changes = await shop.ChangesAfterAsync(watermark, BatchSize, ct);
        if (changes.Count == 0)
        {
            Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: up to date at change {watermark}");
            return;
        }

        // The orders land first and the watermark after them: an attempt that dies between the two replays one batch,
        // which the upsert absorbs; the other order would skip one.
        await store.ApplyAsync(changes, ct);
        var head = changes[^1].ChangeId;
        await ctx.SetVariableAsync("watermark", head, ct);
        Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: {changes.Count} changes applied, watermark {head}");
        await crash.AfterBatchAsync(head, ct);
    }
}
