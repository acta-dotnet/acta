# How to keep another system in sync with a permanent job

One permanent job calls a service on a schedule and keeps a watermark of how far it got. Each firing syncs one bounded batch and returns; the watermark is the job's own durable variable, read at the top of every execution, so a recovered firing continues where the lost one stopped.

- Section: Keep another system in sync, step 1
- Program: `learn/100-scheduling/109-continuous-synchronization` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/109-continuous-synchronization
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Fire the job on a schedule

The schedule fires the one permanent job every five seconds. A firing does one bounded batch and returns, so between firings the job is `Idle` and holds no thread.

The lines that matter:

```csharp
[Job("sync-orders", MaxAttempts = 3, Backoff = "2s")]
[Schedule("every-5s", "PT5S")]
```

In `SyncOrdersJob.cs` (excerpt):

```csharp
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

    await store.ApplyAsync(changes, ct);
    var head = changes[^1].ChangeId;
    await ctx.SetVariableAsync("watermark", head, ct);
    Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: {changes.Count} changes applied, watermark {head}");
    // The lab's switch for killing the worker after a batch; a job of your own has no such line.
    await crash.AfterBatchAsync(head, ct);
}
```

## 2. Read the watermark and ask for what changed after it

The call to the service is a bare line on purpose: a read is safe to repeat, and on a permanent job a step would answer from an earlier firing's record. A firing that finds nothing returns; the next one comes on schedule.

`SyncOrdersJob.cs` (excerpt):

```csharp
var watermark = await ctx.GetVariableOrDefaultAsync("watermark", 0L, ct);
var changes = await shop.ChangesAfterAsync(watermark, BatchSize, ct);
if (changes.Count == 0)
{
    Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: up to date at change {watermark}");
    return;
}
```

## 3. Let the service hand out its changes in a fixed order

The pattern needs one thing from the service: changes come in a fixed order, and a caller can ask for the ones after the last one it saw. An API with a `since` or a cursor parameter fits the same way.

The lines that matter:

```csharp
command.CommandText =
    "SELECT change_id, order_id, status, amount_cents FROM shop_changes WHERE change_id > @since ORDER BY change_id LIMIT @take";
```

In `ShopFeed.cs` (excerpt):

```csharp
await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync(ct);
await using var command = connection.CreateCommand();
command.CommandText =
    "SELECT change_id, order_id, status, amount_cents FROM shop_changes WHERE change_id > @since ORDER BY change_id LIMIT @take";
```

## 4. Save the work first and the watermark after

The orders land first and the watermark after them: an attempt that dies between the two replays one batch, which the upsert absorbs; the other order would skip one.

The lines that matter:

```csharp
await store.ApplyAsync(changes, ct);
var head = changes[^1].ChangeId;
await ctx.SetVariableAsync("watermark", head, ct);
```

In `SyncOrdersJob.cs` (excerpt):

```csharp
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

    await store.ApplyAsync(changes, ct);
    var head = changes[^1].ChangeId;
    await ctx.SetVariableAsync("watermark", head, ct);
    Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: {changes.Count} changes applied, watermark {head}");
    // The lab's switch for killing the worker after a batch; a job of your own has no such line.
    await crash.AfterBatchAsync(head, ct);
}
```

## 5. Make the save an upsert that keeps the newer change

A batch applied twice then changes nothing. The statement is SQLite's; write the same keep-the-newer upsert in your own database's dialect.

The lines that matter:

```csharp
upsert.CommandText = """
    INSERT INTO local_orders (order_id, status, amount_cents, last_change_id) VALUES (@order, @status, @amount, @change)
    ON CONFLICT (order_id) DO UPDATE SET status = excluded.status, amount_cents = excluded.amount_cents, last_change_id = excluded.last_change_id
    WHERE excluded.last_change_id > local_orders.last_change_id
    """;
```

In `OrderStore.cs` (excerpt):

```csharp
await using var upsert = connection.CreateCommand();
upsert.Transaction = (SqliteTransaction)transaction;
upsert.CommandText = """
    INSERT INTO local_orders (order_id, status, amount_cents, last_change_id) VALUES (@order, @status, @amount, @change)
    ON CONFLICT (order_id) DO UPDATE SET status = excluded.status, amount_cents = excluded.amount_cents, last_change_id = excluded.last_change_id
    WHERE excluded.last_change_id > local_orders.last_change_id
    """;
upsert.Parameters.AddWithValue("@order", change.OrderId);
upsert.Parameters.AddWithValue("@status", change.Status);
upsert.Parameters.AddWithValue("@amount", change.AmountCents);
upsert.Parameters.AddWithValue("@change", change.ChangeId);
await upsert.ExecuteNonQueryAsync(ct);
```

## 6. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/109-continuous-synchronization
```

Its output must contain:

```text
[sync-orders] execution 1: 40 changes applied, watermark 40
[sync-orders] execution 3: 40 changes applied, watermark 120
feed head: change 120; local orders: 40 (newest change 120)
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- Never loop forever inside the handler and never poll from it. A firing that finds nothing to do returns, and the next firing comes on schedule.
- The watermark is a durable variable on the job. Acta carries it across firings, retries, and workers, so no table of your own has to remember how far the sync got.
- The service has to hand out changes in a fixed order and keep them. For a source that fires once or forgets, capture it into a durable feed of your own first.
- Firings that came due while no worker ran are skipped, not queued. The next firing reads the watermark and catches up by itself.
- With the default heartbeat, a killed worker's firing comes back three to about seven minutes after its last heartbeat; on SQLite a restart brings it back at once. The lab shortens the heartbeat so you can watch.
- A sync that writes back to the other system needs that system's idempotency key, so a repeated batch does not act twice there.
- `"watermark"` is the key to how far `sync-orders` has read. A renamed variable reads as absent, as if the job had never synchronized.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("sync-orders")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Check yourself

1. The worker is killed right after a batch was saved and the watermark written. What does the next execution do?
   - a) Starts again from change 0
   - b) Reads the watermark the dead attempt wrote and applies the next batch
   - c) Nothing, until someone restarts the job by hand
   - Answer: b. The next worker, or the same one restarted, reclaims the firing, runs the handler from its first line, and reads the watermark. Same job, same firing, one more execution number.
2. An attempt dies after the orders were saved but before the watermark moved. What happens to that batch?
   - a) It is applied a second time, and the upsert changes nothing
   - b) It is skipped
   - c) The job fails for good
   - Answer: a. The retry reads the old watermark and gets the same changes. Each order keeps only a newer change than the one it holds, so the replay leaves the data as it was.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep a value across retries](/learn/203-durable-variable.md)

## More

- [The whole program and its notes](/learn/109-continuous-synchronization.program.md): every file as compiled, 533 lines, and the notes on the design, the rows to inspect, and how to break it.
