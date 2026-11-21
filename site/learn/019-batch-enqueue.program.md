# The whole program: enqueue many jobs in one round trip

The how-to is [/learn/019-batch-enqueue.md](/learn/019-batch-enqueue.md). This file holds every source file of `learn/000-fundamentals/019-batch-enqueue` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### batch-enqueue.cs

```csharp
// How to enqueue many jobs in one round trip
// `EnqueueJobsAsync` writes a whole list of jobs in one round trip to the database and answers their outcomes in
// request order.
using System.Diagnostics;
using Acta;
using Acta.Learn.BatchEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<BatchEnqueueJobs>("batch-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: Build the requests and enqueue them together
// EnqueueJobsAsync writes the whole batch in one round-trip; outcomes return in request order. Two requests with one
// idempotency key in the same batch throw before anything is written.
var requests = Enumerable.Range(1, 500).Select(_ => new EnqueueJobRequest("batch-enqueue", "ping")).ToList();

var sw = Stopwatch.StartNew();
var outcomes = (await jobs.EnqueueJobsAsync(requests)).Jobs;

// learn: end
sw.Stop();
Console.WriteLine(
    $"Enqueued {outcomes.Count} jobs in one round-trip in {sw.ElapsedMilliseconds} ms (ids {outcomes[0].JobId}..{outcomes[^1].JobId})."
);
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.BatchEnqueue
{
    public static class PingJob
    {
        [Job("ping")]
        public static void Handle() { }
    }
}
```

