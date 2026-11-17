# The whole program: pause and resume a job from outside

The how-to is [/learn/309-operator-resume.md](/learn/309-operator-resume.md). This file holds every source file of `learn/300-failure-and-recovery/309-operator-resume` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### operator-resume.cs

```csharp
// How to pause and resume a job from outside
// `IJobs.PauseAsync` and `ResumeAsync` pause and resume a job from outside its handler. Each answers whether it was
// applied or rejected, and its reason lands on the job's timeline.
using Acta;
using Acta.Learn.OperatorResume;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OperatorResumeJobs>("operator-resume");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var queries = host.Services.GetRequiredService<IActaOperations>();

// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var handle = await jobs.ScheduleAsync(new ProcessReport("q3-summary"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Enqueued job {handle.JobRef} with a 5-second delay.");

// learn: 1. Pause the job
// The job is not yet claimable (delayed), so the transition is Applied.
var pauseResult = await jobs.PauseAsync(handle, "review window");

// learn: end
Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

// learn: 2. Resume it
// A resume moves the Paused job back to Ready at the due time it kept, so the worker claims it once the
// 5-second delay is up.
var resumeResult = await jobs.ResumeAsync(handle, "review complete");

// learn: end
Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");

// Poll until the worker finishes the job (terminal status).
JobStatusCode? status;
do
{
    await Task.Delay(300);
    status = (await jobs.GetStatusAsync(handle))?.Status;
} while (status is not null && !status.Value.IsTerminal);

Console.WriteLine($"Job finished with status={status}");

// learn: 3. Read the answer: a finished job refuses
// A pause of a terminal (Succeeded) job is Rejected, because terminal jobs cannot be paused.
var rejectedResult = await jobs.PauseAsync(handle, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

// learn: end

// learn: 4. Read the reasons on the timeline
// Each control leaves an event that carries the reason it was given.
var events = await queries.Ledger.ListEventsAsync(new ListEventsQuery(Job: handle));
foreach (var ev in events.Items)
{
    if (ev.EventCode is EventCode.JobPaused or EventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} message={ev.Message ?? "(none)"} details={ev.DetailsText ?? "(none)"}");
}

// learn: end

await host.StopAsync();

namespace Acta.Learn.OperatorResume
{
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
}
```

