# How to cancel a job from inside its handler

`CancelAsync` in a handler ends the job as `Cancelled`: terminal, not a failure, so no retry and no alert follow.

- Section: Decide what happens when work fails, step 4
- Program: `learn/300-failure-and-recovery/302-cancel-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/302-cancel-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Cancel the job where the work is no longer wanted

CancelAsync ends the job as Cancelled: terminal, not a failure, so no retry and no alert.

The lines that matter:

```csharp
await context.CancelAsync("source feed no longer exists", ct);
```

In `cancel-job.cs` (excerpt):

```csharp
public sealed class ImportFeedJob
{
    [Job("import-feed", MaxAttempts = 5)]
    public async Task Handle(ImportFeed feed, JobContext context, CancellationToken ct)
    {
        if (feed.SourceGone)
        {
            await context.CancelAsync("source feed no longer exists", ct);
        }

        Console.WriteLine($"[{feed.Url}] imported");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/302-cancel-job
```

Its output must contain:

```text
status=Cancelled
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `CancelAsync` ends the job by throwing, so nothing after it runs. `Cancelled` is not a failure: no retry and no alert follow.
- The name in `[Job("import-feed")]` and the namespace in `Run<CancelJobJobs>("cancel-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ImportFeed` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `import-feed-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pause a job from inside its handler](/learn/303-pause-job.md)

## More

- [The whole program](/learn/302-cancel-job.program.md): every file as compiled, 49 lines.
