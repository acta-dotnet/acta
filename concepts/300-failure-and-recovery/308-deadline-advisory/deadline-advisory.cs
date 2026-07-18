using Acta;
using Acta.Concepts.DeadlineAdvisory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeadlineAdvisoryJobs>("deadline-advisory");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Same timing as 307 (2s deadline, first run held 4s), so the job is overdue when the handler starts.
// An Advisory deadline never auto-terminates: the handler runs anyway and decides what to do.
var outcome = await jobs.EnqueueAsync(new GenerateReport("q3"), o => o.Delayed(TimeSpan.FromSeconds(4)));
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] enqueued; 2s deadline, but the first run is held 4s.");

JobSnapshot? snapshot;
do
{
    await Task.Delay(500);
    snapshot = await jobs.GetAsync(outcome);
} while (snapshot is null or { Status: not (JobStatusCode.Done or JobStatusCode.Failed or JobStatusCode.Cancelled) });

// Done: the handler ran past the deadline and chose the reduced path itself.
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] status={snapshot.Status}");

await host.StopAsync();

namespace Acta.Concepts.DeadlineAdvisory
{
    public sealed record GenerateReport(string Quarter);

    public static class GenerateReportJob
    {
        // Advisory makes the deadline informational: unlike Strict (307), the engine never cancels an
        // overdue job. The handler reads ctx.IsOverdue (and ctx.TimeUntilDeadline) and degrades on its
        // own; here it emits a quick summary instead of the expensive full report when it is late.
        [Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
        public static async Task Handle(GenerateReport input, JobContext ctx, CancellationToken ct)
        {
            if (ctx.IsOverdue)
            {
                Console.WriteLine($"[{ctx.JobRef}] running late by {ctx.TimeUntilDeadline}; emitting a quick {input.Quarter} summary.");
                return;
            }

            Console.WriteLine($"[{ctx.JobRef}] on time; building the full {input.Quarter} report...");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Console.WriteLine($"[{ctx.JobRef}] {input.Quarter} report done.");
        }
    }
}
