# How to start each firing of a recurring job clean

A permanent job keeps its variables and step results from one firing to the next. `ResetStateAsync` as the firing's last line clears them, so the next one starts blank.

- Section: Run work on a schedule, step 13
- Program: `learn/200-durable-execution/214-reset-state` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/214-reset-state
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Clear the job's durable state as the last act of the firing

Clear the firing's durable state as its last line, so the next firing starts blank. Never at the top: a reset there also runs on every retry and replay of the same firing and erases its progress.

`reset-state.cs` (excerpt):

```csharp
await context.ResetStateAsync(ct);
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/214-reset-state
```

Its output must contain:

```text
cycle start - 0 readings carried over (0 means last cycle reset)
cycle done  - 3 readings, avg
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `"reading-count"` is the key to the stored count. A renamed variable reads as absent.
- The name in `[Job("collect-metrics")]` and the namespace in `Run<ResetStateJobs>("reset-state")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep another system in sync with a permanent job](/learn/109-continuous-synchronization.md)

## More

- [The whole program](/learn/214-reset-state.program.md): every file as compiled, 53 lines.
