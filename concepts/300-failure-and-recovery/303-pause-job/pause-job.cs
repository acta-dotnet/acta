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
Console.WriteLine("The job is parked. An operator resumes it with IJobs.ResumeAsync when ready.");

await host.StopAsync();

namespace Acta.Concepts.PauseJob
{
    public sealed record ApplyMigration(string Version, bool NeedsApproval);

    public sealed class ApplyMigrationJob
    {
        [Job("apply-migration")]
        public async Task Handle(ApplyMigration migration, JobContext ctx, CancellationToken ct)
        {
            if (migration.NeedsApproval)
            {
                // PauseAsync parks the job as Paused until IJobs.ResumeAsync; resume replays the handler
                // from the top, so re-check the condition before proceeding.
                await ctx.PauseAsync("waiting for operator approval", ct);
            }

            Console.WriteLine($"[{migration.Version}] migration applied");
        }
    }
}
