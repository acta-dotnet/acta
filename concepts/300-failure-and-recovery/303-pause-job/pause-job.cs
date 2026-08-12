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
Console.WriteLine("The job is parked. Resuming it as-is would replay the handler and park it again:");
Console.WriteLine("the approval has to be recorded first, then IJobs.ResumeAsync lets the handler through.");

await host.StopAsync();

namespace Acta.Concepts.PauseJob
{
    public sealed record ApplyMigration(string Version, bool NeedsApproval);

    public sealed class ApplyMigrationJob
    {
        [Job("apply-migration")]
        public async Task Handle(ApplyMigration migration, JobContext context, CancellationToken ct)
        {
            // Resume replays the handler from the top, so the approval cannot live in the input: an
            // immutable NeedsApproval would park the job again on every resume, forever. It lives in a
            // durable variable the operator sets before resuming, which is the whole shape of a
            // human-in-the-loop gate.
            var approved = await context.GetVariableOrDefaultAsync<bool>("approved", ct);
            if (migration.NeedsApproval && !approved)
            {
                await context.PauseAsync("waiting for operator approval", ct);
                return;
            }

            Console.WriteLine($"[{migration.Version}] migration applied");
        }
    }
}
