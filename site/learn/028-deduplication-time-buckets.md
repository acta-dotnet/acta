# How to allow one job per hour or per day

A deduplication key built from a time bucket lets one job in per bucket: `DeduplicationKey.PerTimeBucket` here, with `PerHour` and `PerDay` as its fixed sizes.

- Section: Start jobs, step 10
- Program: `concepts/000-fundamentals/028-deduplication-time-buckets` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/028-deduplication-time-buckets
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Build the key from the time bucket

The key carries the bucket its instant falls in, so every enqueue inside the same half hour composes the same key.

The lines that matter:

```csharp
var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));
```

In `deduplication-time-buckets.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

// Fixed instants keep the buckets deterministic; a per-run account keeps a second run against the same
// local database from deduplicating onto the first run's jobs.
var account = $"acme-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
var bucketSize = TimeSpan.FromMinutes(30);
var instantA = new DateTimeOffset(2025, 6, 1, 10, 00, 00, TimeSpan.Zero); // bucket 10:00-10:30
var instantB = new DateTimeOffset(2025, 6, 1, 10, 45, 00, TimeSpan.Zero); // bucket 10:30-11:00

// --- Within the same bucket: two enqueues collapse to one job ---
Console.WriteLine("Enqueueing twice in the same 30-minute bucket...");

var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));

Console.WriteLine($"  enqueue 1: job {first.JobRef} action={first.Action}");

var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));

Console.WriteLine($"  enqueue 2: job {second.JobRef} action={second.Action}");

Console.WriteLine(
    second.Action == EnqueueJobAction.Deduplicated ? "Deduplicated - same bucket, same job." : "ERROR: expected Deduplicated"
);

// --- Across buckets: a third enqueue in a new bucket inserts a fresh job ---
Console.WriteLine("Enqueueing in the next bucket...");

var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));

Console.WriteLine($"  enqueue 3: job {third.JobRef} action={third.Action}");

Console.WriteLine(third.Action == EnqueueJobAction.Inserted ? "Inserted - new bucket, new job." : "ERROR: expected Inserted");

Console.WriteLine(first.JobRef != third.JobRef ? "Two distinct jobs across buckets - done." : "ERROR: expected distinct jobs");
```

## 2. Enqueue again inside the bucket and get the first job back

The lines that matter:

```csharp
var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));
```

In `deduplication-time-buckets.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

// Fixed instants keep the buckets deterministic; a per-run account keeps a second run against the same
// local database from deduplicating onto the first run's jobs.
var account = $"acme-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
var bucketSize = TimeSpan.FromMinutes(30);
var instantA = new DateTimeOffset(2025, 6, 1, 10, 00, 00, TimeSpan.Zero); // bucket 10:00-10:30
var instantB = new DateTimeOffset(2025, 6, 1, 10, 45, 00, TimeSpan.Zero); // bucket 10:30-11:00

// --- Within the same bucket: two enqueues collapse to one job ---
Console.WriteLine("Enqueueing twice in the same 30-minute bucket...");

// The key carries the bucket its instant falls in, so every enqueue inside the same half hour composes the same key.
var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));

Console.WriteLine($"  enqueue 1: job {first.JobRef} action={first.Action}");

var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));

Console.WriteLine($"  enqueue 2: job {second.JobRef} action={second.Action}");

Console.WriteLine(
    second.Action == EnqueueJobAction.Deduplicated ? "Deduplicated - same bucket, same job." : "ERROR: expected Deduplicated"
);

// --- Across buckets: a third enqueue in a new bucket inserts a fresh job ---
Console.WriteLine("Enqueueing in the next bucket...");

var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));

Console.WriteLine($"  enqueue 3: job {third.JobRef} action={third.Action}");

Console.WriteLine(third.Action == EnqueueJobAction.Inserted ? "Inserted - new bucket, new job." : "ERROR: expected Inserted");

Console.WriteLine(first.JobRef != third.JobRef ? "Two distinct jobs across buckets - done." : "ERROR: expected distinct jobs");
```

## 3. Enqueue in the next bucket and get a new job

The lines that matter:

```csharp
var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));
```

In `deduplication-time-buckets.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

// Fixed instants keep the buckets deterministic; a per-run account keeps a second run against the same
// local database from deduplicating onto the first run's jobs.
var account = $"acme-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
var bucketSize = TimeSpan.FromMinutes(30);
var instantA = new DateTimeOffset(2025, 6, 1, 10, 00, 00, TimeSpan.Zero); // bucket 10:00-10:30
var instantB = new DateTimeOffset(2025, 6, 1, 10, 45, 00, TimeSpan.Zero); // bucket 10:30-11:00

// --- Within the same bucket: two enqueues collapse to one job ---
Console.WriteLine("Enqueueing twice in the same 30-minute bucket...");

// The key carries the bucket its instant falls in, so every enqueue inside the same half hour composes the same key.
var keyA1 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var first = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA1));

Console.WriteLine($"  enqueue 1: job {first.JobRef} action={first.Action}");

var keyA2 = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantA, bucketSize);
var second = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyA2));

Console.WriteLine($"  enqueue 2: job {second.JobRef} action={second.Action}");

Console.WriteLine(
    second.Action == EnqueueJobAction.Deduplicated ? "Deduplicated - same bucket, same job." : "ERROR: expected Deduplicated"
);

// --- Across buckets: a third enqueue in a new bucket inserts a fresh job ---
Console.WriteLine("Enqueueing in the next bucket...");

var keyB = DeduplicationKey.PerTimeBucket("nightly-summary", account, instantB, bucketSize);
var third = await jobs.EnqueueAsync(new NightlySummary("acme"), o => o.DeduplicationKey(keyB));

Console.WriteLine($"  enqueue 3: job {third.JobRef} action={third.Action}");

Console.WriteLine(third.Action == EnqueueJobAction.Inserted ? "Inserted - new bucket, new job." : "ERROR: expected Inserted");

Console.WriteLine(first.JobRef != third.JobRef ? "Two distinct jobs across buckets - done." : "ERROR: expected distinct jobs");
```

## 4. The job it acts on

`deduplication-time-buckets.cs` (excerpt):

```csharp
public sealed record NightlySummary(string Tenant);

public static class NightlySummaryJob
{
    [Job("nightly-summary")]
    public static void Handle(NightlySummary input) => Console.WriteLine($"[{input.Tenant}] summary ran");
}
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/028-deduplication-time-buckets
```

Its output must contain:

```text
Deduplicated - same bucket, same job.
Inserted - new bucket, new job.
Two distinct jobs across buckets - done.
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to find a job by its deduplication key](/learn/014-read-by-deduplication-key.md)

## More

- [The whole program](/learn/028-deduplication-time-buckets.program.md): every file as compiled, 76 lines.
