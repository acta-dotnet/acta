# How to pause a job from inside its handler

`PauseAsync` in a handler parks the job as `Paused` until someone resumes it.

- Section: Decide what happens when work fails, step 5
- Program: `concepts/300-failure-and-recovery/303-pause-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/303-pause-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Pause the job from its handler

Resume replays the handler from the top with the job's current input, so the approval is recorded where the replay reads it: the operator amends the input (IJobs.UpdateInputAsync works on a Paused job) and then resumes. A signal (WaitSignalAsync, RaiseSignalAsync) is the other shape of a human-in-the-loop gate.

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
dotnet run --project concepts/300-failure-and-recovery/303-pause-job
```

Its output must contain:

```text
status=Paused
[v042] migration applied
status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to cut off an attempt that runs too long](/learn/304-execution-timeout.md)

## More

- [The whole program](/learn/303-pause-job.program.md): every file as compiled, 63 lines.
