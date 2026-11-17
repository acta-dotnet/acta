# How to restart a failed job and keep its history

`RestartAsync` runs a finished job again under the same identity. The failure budget is reset, the history is appended to, steps that succeeded are not run again, and the bare lines of the handler are.

- Section: Watch and control running work, step 8
- Program: `concepts/300-failure-and-recovery/310-operator-restart` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/310-operator-restart
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Restart the job under the same identity

RestartAsync re-arms the same job id: resets failure_count, clears retention, moves to Ready.

The lines that matter:

```csharp
var result = await jobs.RestartAsync(outcome, "operator re-ran the report after the data fix");
```

In `operator-restart.cs` (excerpt):

```csharp
}
Console.WriteLine($"terminal: status={snapshot!.Status} failureCount={snapshot.FailureCount} executionNumber={snapshot.ExecutionNumber}");
await ShowRestartStateAsync(lab, "Before restart: one stable identity exhausted its failure budget", outcome.JobId);

FlakyReportJob.DataSourceFixed = true;

var result = await jobs.RestartAsync(outcome, "operator re-ran the report after the data fix");

Console.WriteLine($"restart result: {result.Action} status={result.Status}");

// The worker may already have claimed the row; failure_count is reset regardless of that scheduling race.
snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"immediately observed after restart: status={snapshot!.Status} failureCount={snapshot.FailureCount}");
```

## 2. The job it acts on

`operator-restart.cs` (excerpt):

```csharp
public sealed record FlakyReport(string Id);

public static class FlakyReportJob
{
    private static int _handlerEntries;

    public static bool DataSourceFixed { get; set; }

    // MaxAttempts=3, 0s skips backoff so the budget exhausts quickly.
    [Job("flaky-report", MaxAttempts = 3, Backoff = "0s")]
    public static async Task Handle(FlakyReport input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Id}] bare handler entry #{Interlocked.Increment(ref _handlerEntries)}");
        await context.RunStepAsync(
            "prepare-report",
            _ =>
            {
                Console.WriteLine($"[{input.Id}] preparation step body invoked; its recorded success can be reused");
                return Task.CompletedTask;
            },
            ct
        );

        if (!DataSourceFixed)
        {
            throw new InvalidOperationException($"[{input.Id}] data source unavailable");
        }
        Console.WriteLine($"[{input.Id}] report generated after operator restart");
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/310-operator-restart
```

Its output must contain:

```text
terminal: status=Failed failureCount=3 executionNumber=3
restart result: Applied status=Ready
immediately observed after restart: status=Ready failureCount=0
[report-1] report generated after operator restart
re-run complete: status=Succeeded executionNumber=4
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A restart keeps every succeeded step: those steps return what they computed before.
- A parent that failed on a child fails again when it is restarted alone. Restart the child, then the parent once the child has succeeded.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to control jobs over HTTP](/learn/410-http-api-controls.md)

## More

- [The whole program and its notes](/learn/310-operator-restart.program.md): every file as compiled, 155 lines, and the notes on the design, the rows to inspect, and how to break it.
