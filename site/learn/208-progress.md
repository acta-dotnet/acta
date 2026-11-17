# How to report how far along a job is

`SetProgressAsync` stores how far the job has come as the checkpoint `sys.progress`, which the dashboard lists while the job runs.

- Section: Make work survive a crash, step 7
- Program: `learn/200-durable-execution/208-progress` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/208-progress
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Store the progress as the work advances

Each call replaces the last value. The dashboard shows it with the job's checkpoints; a page of your own reads a variable instead (415).

The lines that matter:

```csharp
await context.SetProgressAsync(percent, ct);
```

In `progress.cs` (excerpt):

```csharp
[Job("import-file")]
public async Task Handle(ImportFile input, JobContext context, CancellationToken ct)
{
    for (var row = 1; row <= input.Rows; row++)
    {
        await Task.Delay(100, ct);

        var percent = row * 100 / input.Rows;

        await context.SetProgressAsync(percent, ct);

        var filled = percent / 10;
        Console.Write($"\r[{input.Name}] [{new string('#', filled)}{new string('-', 10 - filled)}] {percent, 3}%");
    }

    Console.WriteLine();
    Console.WriteLine($"[{input.Name}] import complete");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/208-progress
```

Its output must contain:

```text
[customers.csv] [##########] 100%
[customers.csv] import complete
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `SetProgressAsync` keeps one value, last write wins, as the checkpoint `sys.progress`. A value your own screen reads goes in a variable.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to wait for an approval without holding a worker](/learn/204-wait-signal.md)

## More

- [The whole program](/learn/208-progress.program.md): every file as compiled, 57 lines.
