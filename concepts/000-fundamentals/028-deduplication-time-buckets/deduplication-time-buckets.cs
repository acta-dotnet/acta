// Concept: time-bucketed deduplication keys collapse duplicates within a bucket and allow
// a fresh insert once the bucket advances.
using Acta;
using Acta.Concepts.DeduplicationTimeBuckets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeduplicationTimeBucketsJobs>("deduplication-time-buckets");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Use a fixed instant so the rung is deterministic regardless of wall-clock time.
var bucketSize = TimeSpan.FromMinutes(30);
var instantA = new DateTimeOffset(2025, 6, 1, 10, 00, 00, TimeSpan.Zero); // bucket 10:00-10:30
var instantB = new DateTimeOffset(2025, 6, 1, 10, 45, 00, TimeSpan.Zero); // bucket 10:30-11:00

// --- Within the same bucket: two enqueues collapse to one job ---
Console.WriteLine("Enqueueing twice in the same 30-minute bucket...");

var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", "acme", instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));
Console.WriteLine($"  enqueue 1: job {first.JobRef} action={first.Action}");

var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", "acme", instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));
Console.WriteLine($"  enqueue 2: job {second.JobRef} action={second.Action}");

Console.WriteLine(
    second.Action == JobEnqueueAction.Deduplicated ? "Deduplicated - same bucket, same job." : "ERROR: expected Deduplicated"
);

// --- Across buckets: a third enqueue in a new bucket inserts a fresh job ---
Console.WriteLine("Enqueueing in the next bucket...");

var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", "acme", instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));
Console.WriteLine($"  enqueue 3: job {third.JobRef} action={third.Action}");

Console.WriteLine(third.Action == JobEnqueueAction.Inserted ? "Inserted - new bucket, new job." : "ERROR: expected Inserted");

Console.WriteLine(first.JobRef != third.JobRef ? "Two distinct jobs across buckets - done." : "ERROR: expected distinct jobs");

await host.StopAsync();

namespace Acta.Concepts.DeduplicationTimeBuckets
{
    public sealed record NightlySummary(string Tenant);

    public static class NightlySummaryJob
    {
        [Job("nightly-summary")]
        public static void Handle(NightlySummary input) => Console.WriteLine($"[{input.Tenant}] summary ran");
    }
}
