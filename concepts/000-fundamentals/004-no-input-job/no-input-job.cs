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

// NoInput enqueues a job that takes no input.
await jobs.EnqueueAsync<NoInput>(default);
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.NoInputJob
{
    public static class HeartbeatJob
    {
        [Job("heartbeat")]
        public static Task Handle()
        {
            Console.WriteLine("Heartbeat tick (no input needed).");
            return Task.CompletedTask;
        }
    }
}
