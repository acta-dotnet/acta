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

JobDetail? snapshot;
do
{
    await Task.Delay(500);
    snapshot = await jobs.GetAsync(outcome);
} while (snapshot is null or { Status: not (JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled) });

// Done: the handler ran past the deadline and chose the reduced path itself.
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] status={snapshot.Status}");

await host.StopAsync();

namespace Acta.Concepts.DeadlineAdvisory
{
    public sealed record GenerateReport(string Quarter);

    public static class GenerateReportJob
    {
        // Advisory makes the deadline informational: unlike Strict (307), the engine never cancels an
        // overdue job. The handler reads context.IsOverdue (and context.TimeUntilDeadline) and degrades on its
        // own; here it emits a quick summary instead of the expensive full report when it is late.
        [Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
        public static async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
        {
            if (context.IsOverdue)
            {
                Console.WriteLine(
                    $"[{context.JobRef}] running late by {context.TimeUntilDeadline}; emitting a quick {input.Quarter} summary."
                );
                return;
            }

            Console.WriteLine($"[{context.JobRef}] on time; building the full {input.Quarter} report...");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Console.WriteLine($"[{context.JobRef}] {input.Quarter} report done.");
        }
    }
}
