# How to save a record and start its job in one transaction

`EnqueueAsync` takes your open transaction, so the order row and the job that fulfils it commit together, or neither exists.

- Section: Start jobs, step 11
- Program: `concepts/000-fundamentals/029-enqueue-in-transaction` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/029-enqueue-in-transaction
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Insert the record and enqueue its job on one transaction

The transaction is yours: Acta writes the job through it and never commits it, rolls it back, or retries it. No worker sees the job before the commit.

The lines that matter:

```csharp
EnqueueJobResponse committed;
await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", kept);
    committed = await jobs.EnqueueAsync(transaction, new FulfilOrder(kept));
    await transaction.CommitAsync();
}
```

In `enqueue-in-transaction.cs` (excerpt):

```csharp
await ExecuteAsync(null, "CREATE TABLE IF NOT EXISTS shop_orders (order_id TEXT PRIMARY KEY)");

// A run of its own, so a second run against the same file adds its own two orders.
var run = Guid.NewGuid().ToString("N")[..8];
var (kept, dropped) = ($"order-{run}-kept", $"order-{run}-dropped");

EnqueueJobResponse committed;
await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", kept);
    committed = await jobs.EnqueueAsync(transaction, new FulfilOrder(kept));
    await transaction.CommitAsync();
}

// The answer of an enqueue is provisional until the commit. After a rollback its job ref names nothing.
EnqueueJobResponse rolledBack;
await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", dropped);
```

## 2. Roll back, and neither the record nor the job exists

The answer of an enqueue is provisional until the commit. After a rollback its job ref names nothing.

The lines that matter:

```csharp
EnqueueJobResponse rolledBack;
await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", dropped);
    rolledBack = await jobs.EnqueueAsync(transaction, new FulfilOrder(dropped));
    await transaction.RollbackAsync();
}
```

In `enqueue-in-transaction.cs` (excerpt):

```csharp
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", kept);
    committed = await jobs.EnqueueAsync(transaction, new FulfilOrder(kept));
    await transaction.CommitAsync();
}

EnqueueJobResponse rolledBack;
await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", dropped);
    rolledBack = await jobs.EnqueueAsync(transaction, new FulfilOrder(dropped));
    await transaction.RollbackAsync();
}

await jobs.WaitAsync(committed.JobRef);
Console.WriteLine($"committed:   order row {await OrderExistsAsync(kept)}, job {await JobAsync(committed.JobRef)}");
Console.WriteLine($"rolled back: order row {await OrderExistsAsync(dropped)}, job {await JobAsync(rolledBack.JobRef)}");

await host.StopAsync();
```

## 3. The job it acts on

`enqueue-in-transaction.cs` (excerpt):

```csharp
public sealed record FulfilOrder(string OrderId);

public static class FulfilOrderJob
{
    [Job("fulfil-order")]
    public static void Handle(FulfilOrder order) => Console.WriteLine($"[{order.OrderId[^4..]}] order fulfilled");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/029-enqueue-in-transaction
```

Its output must contain:

```text
[kept] order fulfilled
committed:   order row yes, job succeeded
rolled back: order row no, job none
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The transaction is yours. Acta never opens, commits, rolls back, disposes, or retries it, and any exception inside it means rolling back the whole of it.
- The answer of the enqueue is provisional until the commit and dead after a rollback. Keep or show the job's ref only once the commit has returned.
- No wake-up is published for an enqueue in a transaction, so a worker finds the job on its next poll after the commit.
- There is no `CallAsync` in a transaction and no handle: the job is invisible until the commit, so nothing could wait for it.
- The record's table has to live in the database Acta uses. Work for a separate database is handed over through an outbox.
- A request that may arrive twice still needs a deduplication key on the enqueue, as in [keep a repeated request from creating a second job](/learn/010-deduplication-key.md).

## Check yourself

1. The transaction rolls back after the enqueue. What happens to the job?
   - a) It runs, and fails when it finds no order
   - b) It never existed
   - c) It stays ready until someone cancels it
   - Answer: b. The job's row was written through the same transaction as the order's row, so the rollback took both, and no worker ever saw the job.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to find a job by its deduplication key](/learn/014-read-by-deduplication-key.md)

## More

- [The whole program](/learn/029-enqueue-in-transaction.program.md): every file as compiled, 101 lines.
