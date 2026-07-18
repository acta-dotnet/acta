using Acta;
using Acta.Concepts.DeadlineStrict;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeadlineStrictJobs>("deadline-strict");
});

// Quiet the framework's failure logging; the job is cancelled on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The deadline is 2s after creation, but Delayed holds the first run for 4s, so by the time the
// worker can claim it the job is already overdue. A Strict deadline cancels it at admission and the
// handler never runs. Delayed() and the deadline both resolve on the database clock.
var outcome = await jobs.EnqueueAsync(new GenerateReport("q3"), o => o.Delayed(TimeSpan.FromSeconds(4)));
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] enqueued; 2s deadline, but the first run is held 4s.");

JobSnapshot? snapshot;
do
{
    await Task.Delay(500);
    snapshot = await jobs.GetAsync(outcome);
} while (snapshot is null or { Status: not (JobStatusCode.Done or JobStatusCode.Failed or JobStatusCode.Cancelled) });

// Cancelled (the finishing event reason is JobDeadlineExceeded). FailureCount stays 0: a deadline
// cancel does not consume the retry budget.
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] status={snapshot.Status} failureCount={snapshot.FailureCount}");

await host.StopAsync();

namespace Acta.Concepts.DeadlineStrict
{
    public sealed record GenerateReport(string Quarter);

    public static class GenerateReportJob
    {
        // Deadline is the whole-job budget measured from creation, stable across retries (unlike 304's
        // ExecutionTimeout, which caps a single attempt). Strict is the default: an overdue job is
        // cancelled at admission, and a retry that would land past the deadline is refused re-arm.
        [Job("generate-report", Deadline = "2s")]
        public static void Handle(GenerateReport input) =>
            Console.WriteLine($"building the {input.Quarter} report (never printed; the job was overdue before it ran)");
    }
}
