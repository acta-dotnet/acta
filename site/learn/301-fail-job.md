# How to fail a job for good, with no retry

`FailAsync` ends the job as `Failed` at once, whatever its retry budget. A thrown exception would be retried.

- Section: Decide what happens when work fails, step 3
- Program: `learn/300-failure-and-recovery/301-fail-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/301-fail-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. End the job as failed where retrying cannot help

FailAsync ends the job as Failed now (no retry); a thrown exception retries to MaxAttempts.

The lines that matter:

```csharp
await context.FailAsync("file is corrupt and cannot be processed", ct);
```

In `fail-job.cs` (excerpt):

```csharp
public sealed class ProcessUploadJob
{
    // FailAsync is terminal even with MaxAttempts = 5: it ends the job, it does not retry.
    [Job("process-upload", MaxAttempts = 5)]
    public async Task Handle(ProcessUpload upload, JobContext context, CancellationToken ct)
    {
        if (upload.Corrupt)
        {
            await context.FailAsync("file is corrupt and cannot be processed", ct);
        }

        Console.WriteLine($"[{upload.FileName}] file processed");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/301-fail-job
```

Its output must contain:

```text
status=Failed, failureCount=0
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `FailAsync` ends the job by throwing, so nothing after it runs. The compiler cannot see that: in a handler that returns a value, write `return` or `throw` after it.
- A job ended this way reads `Failed` with `FailureCount` 0: no attempt failed, the handler ended it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to cancel a job from inside its handler](/learn/302-cancel-job.md)

## More

- [The whole program](/learn/301-fail-job.program.md): every file as compiled, 54 lines.
