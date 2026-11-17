# How to let one job at a time touch a resource

`RunWithLockAsync` holds a named lock around a piece of work, so jobs that name the same lock take turns.

- Section: Control order and load, step 1
- Program: `concepts/200-durable-execution/207-run-with-lock` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/207-run-with-lock
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run the work under a named lock

Named lock held for the action; same key serializes both concurrently-running jobs. To exclude the whole job instead, set ConcurrencyKey on enqueue (see 209-concurrency-key).

The lines that matter:

```csharp
await context.RunWithLockAsync(
    "inventory",
    async token =>
    {
        Console.WriteLine($"[{input.Sku}] acquired lock, updating inventory...");
        await Task.Delay(1000, token);
        Console.WriteLine($"[{input.Sku}] done, releasing lock");
    },
    ct: ct
);
```

In `run-with-lock.cs` (excerpt):

```csharp
public sealed class UpdateInventoryJob
{
    [Job("update-inventory")]
    public async Task Handle(UpdateInventory input, JobContext context, CancellationToken ct)
    {
        await context.RunWithLockAsync(
            "inventory",
            async token =>
            {
                Console.WriteLine($"[{input.Sku}] acquired lock, updating inventory...");
                await Task.Delay(1000, token);
                Console.WriteLine($"[{input.Sku}] done, releasing lock");
            },
            ct: ct
        );
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/207-run-with-lock
```

Its output must contain:

```text
[sku-A] done, releasing lock
[sku-B] done, releasing lock
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- Acquiring the lock waits about 30 seconds, the `timeout:` argument, before it throws `LockAcquisitionTimeoutException`.
- The hold renews with the holder's heartbeat, so a dead holder keeps the lock for up to one lease.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to limit how many jobs run at once for one key](/learn/209-concurrency-key.md)

## More

- [The whole program](/learn/207-run-with-lock.program.md): every file as compiled, 53 lines.
