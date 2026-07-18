// Concept: pausing and resuming a job from outside the handler via IJobs.PauseAsync / IJobs.ResumeAsync,
// including a rejected transition on a terminal job and the reason recorded on the event timeline.
using Acta;
using Acta.Concepts.OperatorResume;
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
var queries = host.Services.GetRequiredService<IJobs>();

// Enqueue with a 5-second delay so the job sits in a schedulable state and the pause lands before
// the worker can claim it. The delay is resolved on the database clock (db_now + 5s).
var outcome = await jobs.EnqueueAsync(new ProcessReport("q3-summary"), o => o.Delayed(TimeSpan.FromSeconds(5)));
Console.WriteLine($"Enqueued job {outcome.JobRef} with a 5-second delay.");

// Step 1: pause - job is not yet claimable (delayed), so the transition is Applied.
var pauseResult = await jobs.PauseAsync(outcome, "review window");
Console.WriteLine($"PauseAsync -> action={pauseResult.Action} status={pauseResult.Status}");

// Step 2: resume - moves the Paused job back to Ready so the worker can claim it now.
var resumeResult = await jobs.ResumeAsync(outcome, "review complete");
Console.WriteLine($"ResumeAsync -> action={resumeResult.Action} status={resumeResult.Status}");

// Poll until the worker finishes the job (terminal status).
var jobId = outcome.JobId;
JobStatusCode? status;
do
{
    await Task.Delay(300);
    status = await jobs.GetStatusAsync(JobLookup.ById(jobId));
} while (status is not null && !status.Value.IsTerminal);

Console.WriteLine($"Job finished with status={status}");

// Step 3: attempt to pause a terminal (Done) job - must be Rejected because terminal jobs cannot be paused.
var rejectedResult = await jobs.PauseAsync(outcome, "too late");
Console.WriteLine($"PauseAsync on terminal -> action={rejectedResult.Action} blocking-status={rejectedResult.Status}");

// Step 4: read the event timeline to confirm the pause and resume events with actor and reason.
var events = await queries.ListJobEventsAsync(new ListJobEventsQuery(JobId: jobId));
foreach (var ev in events.Items)
{
    if (ev.EventCode is JobEventCode.JobPaused or JobEventCode.JobResumed)
        Console.WriteLine($"  event={ev.EventCode} actor={ev.ActorCode} reason={ev.ReasonCode} message={ev.ReasonMessage ?? "(none)"}");
}

await host.StopAsync();

namespace Acta.Concepts.OperatorResume
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
