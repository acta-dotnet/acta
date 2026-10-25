// Concept: j.Reference<TManifest>() makes a manifest's jobs typed-enqueueable without
// running a worker - the enqueue-only counterpart of j.Run<TManifest>().

using Acta;
using Acta.Concepts.EnqueueOnlyReference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// In production, these are two separate processes (see demos/ApiWorkerSplit).
// One process here so the rung is self-contained and runnable without a second terminal.

// Worker host: owns the namespace - registers definitions, runs the claim loop, executes jobs.
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
    j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var frontendHost = frontendBuilder.Build();
await frontendHost.StartAsync();

Console.WriteLine("Frontend host started: Reference only - no worker, so no handler runs here.");

// Enqueue from the frontend host; it can reach the job type through the reference.
var frontendJobs = frontendHost.Services.GetRequiredService<IJobs>();
var handle = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));
Console.WriteLine($"Frontend enqueued job {handle.JobRef} (action={handle.Action})");

// WaitAsync returns as soon as the worker finishes the job; the frontend reads the same database.
Console.WriteLine("Waiting for worker to drain...");
await handle.WaitAsync();

var snapshot = await frontendJobs.GetAsync(handle);
Console.WriteLine($"Job status after worker drain: {snapshot!.Status}");

await frontendHost.StopAsync();
await workerHost.StopAsync();

namespace Acta.Concepts.EnqueueOnlyReference
{
    public sealed record WelcomeUser(string Username);

    public static class WelcomeUserJob
    {
        [Job("welcome-user")]
        public static void Handle(WelcomeUser input) => Console.WriteLine($"[worker] executed welcome-user for {input.Username}");
    }
}
