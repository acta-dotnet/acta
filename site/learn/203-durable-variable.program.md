# The whole program: keep a value across retries

The how-to is [/learn/203-durable-variable.md](/learn/203-durable-variable.md). This file holds every source file of `learn/200-durable-execution/203-durable-variable` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### durable-variable.cs

```csharp
// How to keep a value across retries
// A durable variable stores a value with the job the first time it is computed, and every later attempt reads the
// same value.
using Acta;
using Acta.Learn.DurableVariable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableVariableJobs>("durable-variable");
});

// Handler fails once to show retry; quiet the framework failure warning/alert.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new SyncUsers("crm"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.DurableVariable
{
    public sealed record SyncUsers(string Source);

    public sealed class SyncUsersJob
    {
        private static int _attempts;

        [Job("sync-users", MaxAttempts = 2, Backoff = "2s")]
        public async Task Handle(SyncUsers input, JobContext context, CancellationToken ct)
        {
            // learn: Compute the value once and read it back on every attempt
            // Pin the sync window once: GetOrSet stores it on the first pass and replays it on the retry, so the window can't shift between attempts.
            var syncedAt = await context.GetOrSetVariableAsync("synced-at-utc", () => DateTime.UtcNow, ct);
            // learn: end
            Console.WriteLine($"[{input.Source}] syncing changes as of {syncedAt:HH:mm:ss.fff} UTC");

            // Fail the first attempt; the same timestamp comes back on the retry.
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                Console.WriteLine($"[{input.Source}] transient failure - retrying...");
                throw new InvalidOperationException("simulated transient failure");
            }

            Console.WriteLine($"[{input.Source}] sync complete (window pinned at {syncedAt:HH:mm:ss.fff} UTC)");
        }
    }
}
```

