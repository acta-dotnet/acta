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
