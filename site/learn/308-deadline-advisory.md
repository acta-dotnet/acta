# How to run an overdue job in a reduced mode

With `DeadlineBehavior = Advisory` an overdue job still runs. The handler reads `IsOverdue` and chooses a reduced path.

- Section: Decide what happens when work fails, step 9
- Program: `learn/300-failure-and-recovery/308-deadline-advisory` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/308-deadline-advisory
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Make the deadline advisory

Advisory makes the deadline informational: unlike Strict (307), the engine never cancels an overdue job. The handler reads context.IsOverdue (and context.TimeUntilDeadline) and degrades on its own; here it emits a quick summary instead of the expensive full report when it is late. To show an overdue job the program holds the first run past the deadline, as 307 does.

The lines that matter:

```csharp
[Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
```

In `deadline-advisory.cs` (excerpt):

```csharp
public static class GenerateReportJob
{
    [Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
    public static async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
    {
        if (context.IsOverdue)
        {
            Console.WriteLine(
                $"[{context.JobRef}] running late by {context.TimeUntilDeadline}; emitting a quick {input.Quarter} summary."
            );
            return;
        }

        Console.WriteLine($"[{context.JobRef}] on time; building the full {input.Quarter} report...");
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Console.WriteLine($"[{context.JobRef}] {input.Quarter} report done.");
    }
}
```

## 2. Ask whether the job is overdue, and do less when it is

The lines that matter:

```csharp
if (context.IsOverdue)
{
    Console.WriteLine(
        $"[{context.JobRef}] running late by {context.TimeUntilDeadline}; emitting a quick {input.Quarter} summary."
    );
    return;
}
```

In `deadline-advisory.cs` (excerpt):

```csharp
public static class GenerateReportJob
{
    [Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
    public static async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
    {
        if (context.IsOverdue)
        {
            Console.WriteLine(
                $"[{context.JobRef}] running late by {context.TimeUntilDeadline}; emitting a quick {input.Quarter} summary."
            );
            return;
        }

        Console.WriteLine($"[{context.JobRef}] on time; building the full {input.Quarter} report...");
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Console.WriteLine($"[{context.JobRef}] {input.Quarter} report done.");
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/308-deadline-advisory
```

Its output must contain:

```text
emitting a quick q3 summary.
] status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to let one job at a time touch a resource](/learn/207-run-with-lock.md)

## More

- [The whole program](/learn/308-deadline-advisory.program.md): every file as compiled, 69 lines.
