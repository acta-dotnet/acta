# How to set how long a job waits between retries

`Backoff` on the job sets how the wait between retries grows: `1s..8s x2 exact` starts at one second, doubles with each failure, and stops growing at eight.

- Section: Decide what happens when work fails, step 2
- Program: `learn/300-failure-and-recovery/312-backoff-curve` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/312-backoff-curve
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the backoff on the job

Exponential backoff: delays double per failure (1s, 2s, 4s) up to MaxAttempts=4. exact makes the curve deterministic so NextRunAtUtc gaps are observable.

The lines that matter:

```csharp
[Job("flaky-transfer", MaxAttempts = 4, Backoff = "1s..8s x2 exact")]
```

In `backoff-curve.cs` (excerpt):

```csharp
public static class FlakyTransferJob
{
    [Job("flaky-transfer", MaxAttempts = 4, Backoff = "1s..8s x2 exact")]
    public static Task Handle(FlakyTransfer input) =>
        throw new InvalidOperationException($"Transfer {input.Id} failed: upstream unavailable.");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/312-backoff-curve
```

Its output must contain:

```text
failure #3: next retry at
job ended: status=Failed failureCount=4
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- With no `Backoff`, retries back off from a minute to a day over 15 attempts, about 4.4 days in all.
- Between two attempts a job waits in the database under its name. The name in `[Job("flaky-transfer")]` and the namespace in `Run<BackoffCurveJobs>("backoff-curve")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FlakyTransfer` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `flaky-transfer-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to fail a job for good, with no retry](/learn/301-fail-job.md)

## More

- [The whole program](/learn/312-backoff-curve.program.md): every file as compiled, 74 lines.
