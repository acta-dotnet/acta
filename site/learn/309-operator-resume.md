# How to pause and resume a job from outside

`IJobs.PauseAsync` and `ResumeAsync` pause and resume a job from outside its handler. Each answers whether it was applied or rejected, and its reason lands on the job's timeline.

- Section: Watch and control running work, step 7
- Program: `concepts/300-failure-and-recovery/309-operator-resume` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
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
var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var handle = await jobs.ScheduleAsync(new ProcessReport("q3-summary"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Enqueued job {handle.JobRef} with a 5-second delay.");

var pauseResult = await jobs.PauseAsync(handle, "review window");

Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

// A resume moves the Paused job back to Ready at the due time it kept, so the worker claims it once the
// 5-second delay is up.
var resumeResult = await jobs.ResumeAsync(handle, "review complete");

Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");

// Poll until the worker finishes the job (terminal status).
JobStatusCode? status;
do
{
    await Task.Delay(300);
    status = (await jobs.GetStatusAsync(handle))?.Status;
} while (status is not null && !status.Value.IsTerminal);

Console.WriteLine($"Job finished with status={status}");

// A pause of a terminal (Succeeded) job is Rejected, because terminal jobs cannot be paused.
var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

// Read the event timeline to confirm the pause and resume events with their message and details.
var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}
```

## 2. Resume it

A resume moves the Paused job back to Ready at the due time it kept, so the worker claims it once the 5-second delay is up.

The lines that matter:

```csharp
var resumeResult = await jobs.ResumeAsync(handle, "review complete");
```

In `operator-resume.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var handle = await jobs.ScheduleAsync(new ProcessReport("q3-summary"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Enqueued job {handle.JobRef} with a 5-second delay.");

// The job is not yet claimable (delayed), so the transition is Applied.
var pauseResult = await jobs.PauseAsync(handle, "review window");

Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

var resumeResult = await jobs.ResumeAsync(handle, "review complete");

Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");

// Poll until the worker finishes the job (terminal status).
JobStatusCode? status;
do
{
    await Task.Delay(300);
    status = (await jobs.GetStatusAsync(handle))?.Status;
} while (status is not null && !status.Value.IsTerminal);

Console.WriteLine($"Job finished with status={status}");

// A pause of a terminal (Succeeded) job is Rejected, because terminal jobs cannot be paused.
var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

// Read the event timeline to confirm the pause and resume events with their message and details.
var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}
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
var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var handle = await jobs.ScheduleAsync(new ProcessReport("q3-summary"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Enqueued job {handle.JobRef} with a 5-second delay.");

// The job is not yet claimable (delayed), so the transition is Applied.
var pauseResult = await jobs.PauseAsync(handle, "review window");

Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

// A resume moves the Paused job back to Ready at the due time it kept, so the worker claims it once the
// 5-second delay is up.
var resumeResult = await jobs.ResumeAsync(handle, "review complete");

Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");

// Poll until the worker finishes the job (terminal status).
JobStatusCode? status;
do
{
    await Task.Delay(300);
    status = (await jobs.GetStatusAsync(handle))?.Status;
} while (status is not null && !status.Value.IsTerminal);

Console.WriteLine($"Job finished with status={status}");

var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

// Read the event timeline to confirm the pause and resume events with their message and details.
var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}
```

## 4. The job it acts on

`operator-resume.cs` (excerpt):

```csharp
public sealed record ProcessReport(string Name);

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

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/309-operator-resume
```

Its output must contain:

```text
PauseAsync -> action=Applied status=Paused
ResumeAsync -> action=Applied status=Ready
Job finished with status=Succeeded
PauseAsync on terminal -> action=Rejected blocking-status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to restart a failed job and keep its history](/learn/310-operator-restart.md)

## More

- [The whole program](/learn/309-operator-resume.program.md): every file as compiled, 84 lines.
