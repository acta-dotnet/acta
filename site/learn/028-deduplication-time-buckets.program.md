# The whole program: allow one job per hour or per day

The how-to is [/learn/028-deduplication-time-buckets.md](/learn/028-deduplication-time-buckets.md). This file holds every source file of `learn/000-fundamentals/028-deduplication-time-buckets` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### deduplication-time-buckets.cs

```csharp
// How to allow one job per hour or per day
// A deduplication key built from a time bucket lets one job in per bucket: `DeduplicationKey.PerTimeBucket` here,
// with `PerHour` and `PerDay` as its fixed sizes.
using Acta;
using Acta.Learn.DeduplicationTimeBuckets;
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

// Fixed instants keep the buckets deterministic; a per-run account keeps a second run against the same
// local database from deduplicating onto the first run's jobs.
var account = $"acme-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
var bucketSize = TimeSpan.FromMinutes(30);
var instantA = new DateTimeOffset(2025, 6, 1, 10, 00, 00, TimeSpan.Zero); // bucket 10:00-10:30
var instantB = new DateTimeOffset(2025, 6, 1, 10, 45, 00, TimeSpan.Zero); // bucket 10:30-11:00

// --- Within the same bucket: two enqueues collapse to one job ---
Console.WriteLine("Enqueueing twice in the same 30-minute bucket...");

// learn: 1. Build the key from the time bucket
// The key carries the bucket its instant falls in, so every enqueue inside the same half hour composes the same key.
// `bucketSize` is thirty minutes and `instantA` a fixed instant, so the run repeats. `PerHour(job, businessKey)` and
// `PerDay(job, businessKey)` are the fixed sizes, and every bucket is counted in UTC.
var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));

// learn: end
Console.WriteLine($"  enqueue 1: job {first.JobRef} action={first.Action}");

// learn: 2. Enqueue again inside the bucket and get the first job back
var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));

// learn: end
Console.WriteLine($"  enqueue 2: job {second.JobRef} action={second.Action}");

Console.WriteLine(
    second.Action == EnqueueJobAction.Deduplicated ? "Deduplicated - same bucket, same job." : "ERROR: expected Deduplicated"
);

// --- Across buckets: a third enqueue in a new bucket inserts a fresh job ---
Console.WriteLine("Enqueueing in the next bucket...");

// learn: 3. Enqueue in the next bucket and get a new job
var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));

// learn: end
Console.WriteLine($"  enqueue 3: job {third.JobRef} action={third.Action}");

Console.WriteLine(third.Action == EnqueueJobAction.Inserted ? "Inserted - new bucket, new job." : "ERROR: expected Inserted");

Console.WriteLine(first.JobRef != third.JobRef ? "Two distinct jobs across buckets - done." : "ERROR: expected distinct jobs");

await host.StopAsync();

namespace Acta.Learn.DeduplicationTimeBuckets
{
    public sealed record NightlySummary(string Tenant);

    public static class NightlySummaryJob
    {
        [Job("nightly-summary")]
        public static void Handle(NightlySummary input) => Console.WriteLine($"[{input.Tenant}] summary ran");
    }
}
```

