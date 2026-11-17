# How to start each firing of a recurring job clean

A permanent job keeps its variables from one firing to the next. `ResetStateAsync` at the end of a firing clears them, so the next one starts blank.

- Section: Run work on a schedule, step 10
- Program: `concepts/200-durable-execution/214-reset-state` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/214-reset-state
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Clear the job's durable state as the last act of the firing

Clear this cycle's durable state so the next fire starts blank.

The lines that matter:

```csharp
await context.ResetStateAsync(ct);
```

In `reset-state.cs` (excerpt):

```csharp
public readonly record struct CollectMetrics;

public sealed class MetricsMonitor
{
    // Each fire re-runs the same durable job, so variables persist across fires unless the handler
    // clears its durable state as the final act of the cycle.
    [Job("collect-metrics", EventRecording = EventRecordingCode.Failures)]
    [Schedule("every-5-seconds", "PT5S")]
    public async Task Handle(CollectMetrics input, JobContext context, CancellationToken ct)
    {
        // Reads 0 because the previous cycle's reset wiped this variable; without it, reads 3.
        var carried = await context.GetVariableOrDefaultAsync("reading-count", 0, ct);
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC cycle start - {carried} readings carried over (0 means last cycle reset)");

        // Write each reading's count to durable state, so the reset below has something to clear.
        var sum = 0;
        for (var i = 1; i <= 3; i++)
        {
            var reading = Random.Shared.Next(18, 25);
            sum += reading;
            await context.SetVariableAsync("reading-count", i, ct);
            await Task.Delay(100, ct);
        }
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC cycle done  - 3 readings, avg {sum / 3}C");

        await context.ResetStateAsync(ct);
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/214-reset-state
```

Its output must contain:

```text
cycle start - 0 readings carried over (0 means last cycle reset)
cycle done  - 3 readings, avg
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep another system in sync with a permanent job](/learn/109-continuous-synchronization.md)

## More

- [The whole program](/learn/214-reset-state.program.md): every file as compiled, 52 lines.
