# How to pause a job from inside its handler

`PauseAsync` in a handler parks the job as `Paused` until someone resumes it.

- Section: Decide what happens when work fails, step 5
- Program: `learn/300-failure-and-recovery/303-pause-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/303-pause-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Pause the job from its handler

`PauseAsync` ends the attempt where it stands and parks the job as `Paused` until someone resumes it. The lines after it run only after the resume, when the handler starts again from its first line.

The lines that matter:

```csharp
if (migration.NeedsApproval)
{
    await context.PauseAsync("waiting for operator approval", ct);
    return;
}
```

In `pause-job.cs` (excerpt):

```csharp
public sealed class ApplyMigrationJob
{
    [Job("apply-migration")]
    public async Task Handle(ApplyMigration migration, JobContext context, CancellationToken ct)
    {
        if (migration.NeedsApproval)
        {
            await context.PauseAsync("waiting for operator approval", ct);
            return;
        }

        Console.WriteLine($"[{migration.Version}] migration applied");
    }
}
```

## 2. Record the approval in the input, then resume

Resume replays the handler from the top with the job's current input, so a plain resume would park the job again. The operator records the approval first: `IJobs.UpdateInputAsync` works on a Paused job, and the resume then reads the updated input. `outcome` is the handle the enqueue answered. A signal (`WaitSignalAsync`, `RaiseSignalAsync`) is the other shape of a gate a person opens.

The lines that matter:

```csharp
await jobs.UpdateInputAsync(outcome, JobPayload.Json(new ApplyMigration("v042", NeedsApproval: false)), "approved by ops");
await jobs.ResumeAsync(outcome, "approved");
```

In `pause-job.cs` (excerpt):

```csharp
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");
Console.WriteLine("The job is parked. Resuming it as-is would replay the handler and park it again, so the");
Console.WriteLine("operator records the approval in the input first, then resumes.");

await jobs.UpdateInputAsync(outcome, JobPayload.Json(new ApplyMigration("v042", NeedsApproval: false)), "approved by ops");
await jobs.ResumeAsync(outcome, "approved");
await Task.Delay(500);
Console.WriteLine($"status={(await jobs.GetAsync(outcome))!.Status}");

await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/303-pause-job
```

Its output must contain:

```text
status=Paused
[v042] migration applied
status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- An operator can pause, resume or restart a job long after it was enqueued, and the job asks for its handler by name. The name in `[Job("apply-migration")]` and the namespace in `Run<PauseJobJobs>("pause-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ApplyMigration` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `apply-migration-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to cut off an attempt that runs too long](/learn/304-execution-timeout.md)

## More

- [The whole program](/learn/303-pause-job.program.md): every file as compiled, 66 lines.
