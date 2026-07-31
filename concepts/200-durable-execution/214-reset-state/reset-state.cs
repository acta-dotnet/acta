using Acta;
using Acta.Concepts.ResetState;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ResetStateJobs>("reset-state");
});

// The [JobSchedule] recurs every 5s, so there's nothing to enqueue; just run the worker.
await builder.Build().RunAsync();

namespace Acta.Concepts.ResetState
{
    public readonly record struct CollectMetrics;

    public sealed class MetricsMonitor
    {
        // Each fire re-runs the same durable job, so variables persist across fires unless the handler
        // clears its durable state as the final act of the cycle.
        [Job("collect-metrics", AuditLevel = JobAuditLevelCode.Off)]
        [JobSchedule("every-5-seconds", "PT5S")]
        public async Task Handle(CollectMetrics input, JobContext context, CancellationToken ct)
        {
            // Reads 0 because the previous cycle's reset wiped this variable; without it, reads 3.
            var carried = await context.GetVariableOrDefaultAsync("reading-count", 0, ct);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} cycle start - {carried} readings carried over (0 means last cycle reset)");

            // Checkpoint into durable state so a crash mid-cycle resumes from the last reading.
            var sum = 0;
            for (var i = 1; i <= 3; i++)
            {
                var reading = Random.Shared.Next(18, 25);
                sum += reading;
                await context.SetVariableAsync("reading-count", i, ct);
                await Task.Delay(100, ct);
            }
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} cycle done  - 3 readings, avg {sum / 3}C");

            // Clear this cycle's durable state so the next fire starts blank.
            await context.ResetStateAsync(ct);
        }
    }
}
