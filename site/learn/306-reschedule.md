# How to check again later without spending a retry

`RescheduleAsync` ends the execution and runs the handler again after a delay, without counting as a failure or spending a retry.

- Section: Keep another system in sync, step 3
- Program: `concepts/300-failure-and-recovery/306-reschedule` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/306-reschedule
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Reschedule when the answer is not there yet

RescheduleAsync re-arms the job after a delay without burning the retry budget; the handler stops here and replays on the next run (RescheduleUntilAsync takes an absolute time).

The lines that matter:

```csharp
Console.WriteLine($"[{input.Url}] not ready, rescheduling...");
await context.RescheduleAsync(TimeSpan.FromSeconds(1), "resource not ready", ct);
```

In `reschedule.cs` (excerpt):

```csharp
public sealed record PollResource(string Url);

public sealed class PollResourceJob
{
    private static int _tries;

    [Job("poll-resource")]
    public async Task Handle(PollResource input, JobContext context, CancellationToken ct)
    {
        var ready = Interlocked.Increment(ref _tries) >= 2;
        if (!ready)
        {
            Console.WriteLine($"[{input.Url}] not ready, rescheduling...");
            await context.RescheduleAsync(TimeSpan.FromSeconds(1), "resource not ready", ct);
        }

        Console.WriteLine($"[{input.Url}] ready - processed");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/306-reschedule
```

Its output must contain:

```text
[api.example.com/report] not ready, rescheduling...
[api.example.com/report] ready - processed
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep finished work from repeating on a retry](/learn/202-durable-step.md)

## More

- [The whole program](/learn/306-reschedule.program.md): every file as compiled, 53 lines.
