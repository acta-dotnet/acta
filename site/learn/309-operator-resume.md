# How to pause and resume a job from outside

`IJobs.PauseAsync` and `ResumeAsync` pause and resume a job from outside its handler. Each answers whether it was applied or rejected, and its reason lands on the job's timeline.

- Section: Watch and control running work, step 7
- Program: `learn/300-failure-and-recovery/309-operator-resume` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/309-operator-resume
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Pause the job

The job is not yet claimable (delayed), so the transition is Applied.

The lines that matter:

```csharp
var pauseResult = await jobs.PauseAsync(handle, "review window");
```

In `operator-resume.cs` (excerpt):

```csharp
// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var handle = await jobs.ScheduleAsync(new ProcessReport("q3-summary"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Enqueued job {handle.JobRef} with a 5-second delay.");

var pauseResult = await jobs.PauseAsync(handle, "review window");
Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

var resumeResult = await jobs.ResumeAsync(handle, "review complete");
Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");
```

## 2. Resume it

A resume moves the Paused job back to Ready at the due time it kept, so the worker claims it once the 5-second delay is up.

The lines that matter:

```csharp
var resumeResult = await jobs.ResumeAsync(handle, "review complete");
```

In `operator-resume.cs` (excerpt):

```csharp
var pauseResult = await jobs.PauseAsync(handle, "review window");
Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

var resumeResult = await jobs.ResumeAsync(handle, "review complete");
Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");
```

## 3. Read the answer: a finished job refuses

A pause of a terminal (Succeeded) job is Rejected, because terminal jobs cannot be paused.

The lines that matter:

```csharp
var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");
```

In `operator-resume.cs` (excerpt):

```csharp
Console.WriteLine($"Job finished with status={status}");

var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");
```

## 4. Read the reasons on the timeline

Each control leaves an event that carries the reason it was given.

The lines that matter:

```csharp
var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}
```

In `operator-resume.cs` (excerpt):

```csharp
Console.WriteLine($"Job finished with status={status}");

var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}

await host.StopAsync();
```

## 5. The job it acts on

`operator-resume.cs` (excerpt):

```csharp
public sealed class ProcessReportJob
{
    [Job("process-report")]
    public async Task Handle(ProcessReport input, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Name}] processing started");
        await Task.Delay(200, ct);
        Console.WriteLine($"[{input.Name}] processing done");
    }
}
```

## 6. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/300-failure-and-recovery/309-operator-resume
```

Its output must contain:

```text
PauseAsync -> action=Applied status=Paused
ResumeAsync -> action=Applied status=Ready
Job finished with status=Succeeded
PauseAsync on terminal -> action=Rejected blocking-status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- An operator can pause, resume or restart a job long after it was enqueued, and the job asks for its handler by name. The name in `[Job("process-report")]` and the namespace in `Run<OperatorResumeJobs>("operator-resume")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to restart a failed job and keep its history](/learn/310-operator-restart.md)

## More

- [The whole program](/learn/309-operator-resume.program.md): every file as compiled, 87 lines.
