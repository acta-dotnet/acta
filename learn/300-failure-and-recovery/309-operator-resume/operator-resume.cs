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
