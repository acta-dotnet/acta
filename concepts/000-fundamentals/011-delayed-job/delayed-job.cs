using Acta;
using Acta.Concepts.DelayedJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DelayedJobJobs>("delayed-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Delayed() resolves on the database clock (db_now + 5s); this host's clock does not affect timing.
var outcome = await jobs.EnqueueAsync(new SendReminder("Sam"), o => o.Delayed(TimeSpan.FromSeconds(5)));
Console.WriteLine($"Job {outcome.JobRef} enqueued at {DateTime.UtcNow:HH:mm:ss}; runs in ~5s.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.DelayedJob
{
    public sealed record SendReminder(string Name);

    public static class ReminderJob
    {
        [Job("send-reminder")]
        public static void Handle(SendReminder input) =>
            Console.WriteLine($"Reminder fired for {input.Name} at {DateTime.UtcNow:HH:mm:ss}.");
    }
}
