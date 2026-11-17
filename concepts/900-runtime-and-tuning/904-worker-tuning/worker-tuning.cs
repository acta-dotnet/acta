// How to set how many jobs a worker runs at once
// `MaxConcurrentExecutors` caps how many jobs one worker runs at once, and `SafetyPollInterval` bounds how long an
// idle worker sleeps between looks at the database.
using Acta;
using Acta.Concepts.WorkerTuning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const int Cap = 3;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.ConfigureOptions(o =>
    {
        // learn: 1. Cap how many jobs the worker runs at once
        // Cap simultaneous in-flight executions at Cap. Each claim takes only as many rows as there are
        // free executors, so excess work stays Ready in the database until an executor frees. Fixed at startup;
        // not autoscaled.
        o.MaxConcurrentExecutors = Cap;
        // learn: end

        // learn: 2. Bound how long an idle worker sleeps
        // Upper bound on idle claim-loop sleep. Minimum allowed value is 1s (the validator rejects
        // lower values as a DB-traffic guard). The wakeup signal fires immediately after the batch
        // enqueue below, so the poll interval is not on the critical path for this run.
        o.SafetyPollInterval = TimeSpan.FromSeconds(1);
        // learn: end
    });

    j.Run<WorkerTuningJobs>("worker-tuning");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Enqueue a batch; the wakeup signal fires as soon as the first claim poll reads them.
const int BatchSize = 12;
var requests = Enumerable
    .Range(1, BatchSize)
    .Select(i => new EnqueueJobRequest("worker-tuning", "slow-task", JobPayload.Json(new SlowTask(i))))
    .ToList();

Console.WriteLine($"Enqueuing {BatchSize} jobs (cap={Cap})...");
await jobs.EnqueueJobsAsync(requests);
Console.WriteLine("Batch enqueued. Worker is draining.");

// Wait until all jobs have run; the handler increments/decrements a counter so we can observe
// the peak concurrency without any intrusion into the framework internals.
while (SlowTaskJob.Completed < BatchSize)
{
    await Task.Delay(50);
}

Console.WriteLine($"All {BatchSize} jobs done. Peak observed concurrency: {SlowTaskJob.PeakConcurrency} (cap={Cap}).");
Console.WriteLine($"Concurrency cap respected: {SlowTaskJob.PeakConcurrency <= Cap}");

await host.StopAsync();

namespace Acta.Concepts.WorkerTuning
{
    public sealed record SlowTask(int Id);

    public static class SlowTaskJob
    {
        // Active tracks simultaneous in-flight handlers; Completed counts finished ones.
        // Both are updated atomically so the peak observation is race-free.
        private static int _active;
        private static int _completed;
        private static int _peak;

        public static int Completed => Volatile.Read(ref _completed);
        public static int PeakConcurrency => Volatile.Read(ref _peak);

        // Each handler holds its executor for 50 ms - long enough that Cap parallel handlers
        // overlap and the peak counter exceeds 1.
        [Job("slow-task")]
        public static async Task Handle(SlowTask input, CancellationToken ct)
        {
            // Increment active count; update peak if this is a new high.
            var active = Interlocked.Increment(ref _active);
            int peak;
            do
            {
                peak = Volatile.Read(ref _peak);
                if (active <= peak)
                    break;
            } while (Interlocked.CompareExchange(ref _peak, active, peak) != peak);

            Console.WriteLine($"[{input.Id}] started (active={active})");

            await Task.Delay(50, ct);

            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
            Console.WriteLine($"[{input.Id}] done");
        }
    }
}
