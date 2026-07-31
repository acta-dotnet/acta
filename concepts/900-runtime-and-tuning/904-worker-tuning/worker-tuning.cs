// Concept: ConfigureOptions knobs - MaxConcurrentExecutors, ClaimBatchSize, SafetyPollInterval,
// ExecutionProfile.Direct - and how the worker respects the concurrency cap.
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
        // Cap simultaneous in-flight executions at Cap; excess claims wait in the dispatch channel
        // until a slot opens. Fixed at startup; not autoscaled.
        o.MaxConcurrentExecutors = Cap;

        // Pull at most 8 Ready rows per claim poll; keeps the dispatch channel fed without
        // overloading the database on a small batch.
        o.ClaimBatchSize = 8;

        // Upper bound on idle claim-loop sleep. Minimum allowed value is 1s (the validator rejects
        // lower values as a DB-traffic guard). The wakeup signal fires immediately after the batch
        // enqueue below, so the poll interval is not on the critical path for this run.
        o.SafetyPollInterval = TimeSpan.FromSeconds(1);

        // Direct profile: combined claim-execute path (no Dispatched visibility window). Bulk is a
        // separate profile for re-runnable high-volume work; it relaxes completion durability.
        o.ExecutionProfile = ExecutionProfile.Direct;
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
    .Select(i => new JobEnqueueRequest("worker-tuning", "slow-task", JobPayload.Json(new SlowTask(i))))
    .ToList();

Console.WriteLine($"Enqueuing {BatchSize} jobs (cap={Cap}, batchSize=8, profile=Direct)...");
await jobs.EnqueueBatchAsync(requests);
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

        // Each handler holds the slot for 50 ms - long enough that Cap parallel handlers
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
