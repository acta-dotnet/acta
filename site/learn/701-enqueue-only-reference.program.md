# The whole program: enqueue from a process that runs no worker

The how-to is [/learn/701-enqueue-only-reference.md](/learn/701-enqueue-only-reference.md). This file holds every source file of `learn/700-topology-and-deployment/701-enqueue-only-reference` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### enqueue-only-reference.cs

```csharp
// How to enqueue from a process that runs no worker
// `Reference<TManifest>()` lets a process enqueue a namespace's jobs by type without running a worker for it.
// `Run<TManifest>()` is the process that executes them.
using Acta;
using Acta.Learn.EnqueueOnlyReference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// In production, these are two separate processes (see demos/ApiWorkerSplit).
// One process here so the how-to is self-contained and runnable without a second terminal.

// Worker host: owns the namespace - declares its jobs, runs the claim loop, executes them.
var workerBuilder = Host.CreateApplicationBuilder(args);
workerBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(workerBuilder.Configuration);
    j.Run<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var workerHost = workerBuilder.Build();
await workerHost.StartAsync();

Console.WriteLine("Worker host started: owns the namespace, runs the claim loop.");

// Frontend host: Reference only - no claim loop, so it never runs a handler. This demo keeps both hosts in one
// assembly; a real frontend references only the shared manifest and contracts.
// Any process that can reach the same database and knows the manifest type can enqueue.
var frontendBuilder = Host.CreateApplicationBuilder(args);
frontendBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(frontendBuilder.Configuration);
    // learn: 1. Reference the jobs in the process that only enqueues
    // `Reference` makes the jobs enqueueable by type and starts no claim loop, so this process never runs a handler.
    j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
    // learn: end
});
using var frontendHost = frontendBuilder.Build();
await frontendHost.StartAsync();

Console.WriteLine("Frontend host started: Reference only - no worker, so no handler runs here.");

// Enqueue from the frontend host; it can reach the job type through the reference.
var frontendJobs = frontendHost.Services.GetRequiredService<IJobs>();

// learn: 2. Enqueue as usual
// The job is a row in the shared database; the worker process claims it from there.
var handle = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));

// learn: end
Console.WriteLine($"Frontend enqueued job {handle.JobRef} (action={handle.Action})");

// WaitAsync returns as soon as the worker finishes the job; the frontend reads the same database.
Console.WriteLine("Waiting for worker to drain...");
await handle.WaitAsync();

var snapshot = await frontendJobs.GetAsync(handle);
Console.WriteLine($"Job status after worker drain: {snapshot!.Status}");

await frontendHost.StopAsync();
await workerHost.StopAsync();

namespace Acta.Learn.EnqueueOnlyReference
{
    public sealed record WelcomeUser(string Username);

    public static class WelcomeUserJob
    {
        [Job("welcome-user")]
        public static void Handle(WelcomeUser input) => Console.WriteLine($"[worker] executed welcome-user for {input.Username}");
    }
}
```

