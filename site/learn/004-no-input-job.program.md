# The whole program: write a job that takes no input

The how-to is [/learn/004-no-input-job.md](/learn/004-no-input-job.md). This file holds every source file of `concepts/000-fundamentals/004-no-input-job` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### no-input-job.cs

```csharp
// How to write a job that takes no input
// A job with no input: a handler with no parameters, enqueued by its generated call.
using Acta;
using Acta.Concepts.NoInputJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<NoInputJobJobs>("no-input-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 2. Enqueue it by its generated call
// The manifest's generated call names the job; a job without input takes no payload.
await jobs.EnqueueAsync(NoInputJobJobs.Heartbeat());

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.NoInputJob
{
    public static class HeartbeatJob
    {
        // learn: 1. Write a handler with no parameters
        // With no input there is no type to enqueue by, so the job is named by the call the manifest generates for it.
        [Job("heartbeat")]
        public static Task Handle()
        {
            Console.WriteLine("Heartbeat tick (no input needed).");
            return Task.CompletedTask;
        }
        // learn: end
    }
}
```

