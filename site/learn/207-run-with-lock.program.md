# The whole program: let one job at a time touch a resource

The how-to is [/learn/207-run-with-lock.md](/learn/207-run-with-lock.md). This file holds every source file of `concepts/200-durable-execution/207-run-with-lock` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### run-with-lock.cs

```csharp
// How to let one job at a time touch a resource
// `RunWithLockAsync` holds a named lock around a piece of work, so jobs that name the same lock take turns.
using Acta;
using Acta.Concepts.RunWithLock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RunWithLockJobs>("run-with-lock");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Both jobs share a lock key, so they take turns.
await jobs.EnqueueAsync(new UpdateInventory("sku-A"));
await jobs.EnqueueAsync(new UpdateInventory("sku-B"));
Console.WriteLine("Enqueued 2 jobs. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.RunWithLock
{
    public sealed record UpdateInventory(string Sku);

    public sealed class UpdateInventoryJob
    {
        [Job("update-inventory")]
        public async Task Handle(UpdateInventory input, JobContext context, CancellationToken ct)
        {
            // learn: Run the work under a named lock
            // Named lock held for the action; same key serializes both concurrently-running jobs. To exclude the whole job instead, set ConcurrencyKey on enqueue (see 209-concurrency-key).
            await context.RunWithLockAsync(
                "inventory",
                async token =>
                {
                    Console.WriteLine($"[{input.Sku}] acquired lock, updating inventory...");
                    await Task.Delay(1000, token);
                    Console.WriteLine($"[{input.Sku}] done, releasing lock");
                },
                ct: ct
            );
            // learn: end
        }
    }
}
```

