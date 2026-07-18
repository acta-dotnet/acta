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

// Frontend host: Reference only - no handler code loaded, no claim loop.
// Any process that can reach the same database and knows the manifest type can enqueue.
var frontendBuilder = Host.CreateApplicationBuilder(args);
frontendBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(frontendBuilder.Configuration);
    j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var frontendHost = frontendBuilder.Build();
await frontendHost.StartAsync();

Console.WriteLine("Frontend host started: Reference only - no worker, no handlers.");

// Enqueue from the frontend host; it can reach the job type through the reference.
var frontendJobs = frontendHost.Services.GetRequiredService<IJobs>();
var outcome = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));
Console.WriteLine($"Frontend enqueued job {outcome.JobRef} (action={outcome.Action})");

// Poll until the worker finishes the job; polling lets the rung exit as soon as the job
// is done rather than relying on a fixed sleep.
Console.WriteLine("Waiting for worker to drain...");
var workerJobs = workerHost.Services.GetRequiredService<IJobs>();
while (true)
{
    var status = await workerJobs.GetStatusAsync(outcome);
    if (status is { } s && s.IsTerminal)
    {
        break;
    }
    await Task.Delay(100);
}

var snapshot = await workerJobs.GetAsync(outcome);
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
