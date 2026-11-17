# The whole program: pause a job from inside its handler

The how-to is [/learn/303-pause-job.md](/learn/303-pause-job.md). This file holds every source file of `concepts/300-failure-and-recovery/303-pause-job` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### pause-job.cs

```csharp
// How to pause a job from inside its handler
// `PauseAsync` in a handler parks the job as `Paused` until someone resumes it.
using Acta;
using Acta.Concepts.PauseJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PauseJobJobs>("pause-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new ApplyMigration("v042", NeedsApproval: true));
await Task.Delay(500);

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"status={snapshot!.Status}");
Console.WriteLine("The job is parked. Resuming it as-is would replay the handler and park it again, so the");
Console.WriteLine("operator records the approval in the input first, then resumes.");

// learn: 2. Record the approval in the input, then resume
await jobs.UpdateInputAsync(outcome, JobPayload.Json(new ApplyMigration("v042", NeedsApproval: false)), "approved by ops");
await jobs.ResumeAsync(outcome, "approved");

// learn: end
await Task.Delay(500);
Console.WriteLine($"status={(await jobs.GetAsync(outcome))!.Status}");

await host.StopAsync();

namespace Acta.Concepts.PauseJob
{
    public sealed record ApplyMigration(string Version, bool NeedsApproval);

    public sealed class ApplyMigrationJob
    {
        [Job("apply-migration")]
        public async Task Handle(ApplyMigration migration, JobContext context, CancellationToken ct)
        {
            // learn: 1. Pause the job from its handler
            // Resume replays the handler from the top with the job's current input, so the approval is
            // recorded where the replay reads it: the operator amends the input (IJobs.UpdateInputAsync
            // works on a Paused job) and then resumes. A signal (WaitSignalAsync, RaiseSignalAsync) is the
            // other shape of a human-in-the-loop gate.
            if (migration.NeedsApproval)
            {
                await context.PauseAsync("waiting for operator approval", ct);
                return;
            }
            // learn: end

            Console.WriteLine($"[{migration.Version}] migration applied");
        }
    }
}
```

