# How to keep a value across retries

A durable variable stores a value with the job the first time it is computed, and every later attempt reads the same value.

- Section: Keep another system in sync, step 2
- Program: `learn/200-durable-execution/203-durable-variable` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/203-durable-variable
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Compute the value once and read it back on every attempt

Pin the sync window once: GetOrSet stores it on the first pass and replays it on the retry, so the window can't shift between attempts.

The lines that matter:

```csharp
var syncedAt = await context.GetOrSetVariableAsync("synced-at-utc", () => DateTime.UtcNow, ct);
```

In `durable-variable.cs` (excerpt):

```csharp
public sealed class SyncUsersJob
{
    private static int _attempts;

    [Job("sync-users", MaxAttempts = 2, Backoff = "2s")]
    public async Task Handle(SyncUsers input, JobContext context, CancellationToken ct)
    {
        var syncedAt = await context.GetOrSetVariableAsync("synced-at-utc", () => DateTime.UtcNow, ct);
        Console.WriteLine($"[{input.Source}] syncing changes as of {syncedAt:HH:mm:ss.fff} UTC");

        // Fail the first attempt; the same timestamp comes back on the retry.
        if (Interlocked.Increment(ref _attempts) == 1)
        {
            Console.WriteLine($"[{input.Source}] transient failure - retrying...");
            throw new InvalidOperationException("simulated transient failure");
        }

        Console.WriteLine($"[{input.Source}] sync complete (window pinned at {syncedAt:HH:mm:ss.fff} UTC)");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/203-durable-variable
```

Its output must contain:

```text
[crm] transient failure - retrying...
[crm] sync complete (window pinned at
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to check again later without spending a retry](/learn/306-reschedule.md)

## More

- [The whole program](/learn/203-durable-variable.program.md): every file as compiled, 59 lines.
