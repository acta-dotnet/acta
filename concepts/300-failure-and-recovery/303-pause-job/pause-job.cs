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

await jobs.UpdateJobInputAsync(outcome, JobPayload.Json(new ApplyMigration("v042", NeedsApproval: false)), "approved by ops");
await jobs.ResumeAsync(outcome, "approved");
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
            // Resume replays the handler from the top with the job's current input, so the approval is
            // recorded where the replay reads it: the operator amends the input (IJobs.UpdateJobInputAsync
            // works on a Paused job) and then resumes. A signal (WaitSignalAsync, RaiseSignalAsync) is the
            // other shape of a human-in-the-loop gate.
            if (migration.NeedsApproval)
            {
                await context.PauseAsync("waiting for operator approval", ct);
                return;
            }

            Console.WriteLine($"[{migration.Version}] migration applied");
        }
    }
}
