// How to run a job once, later
// `ScheduleAsync(input, delay)` stores the job now and makes it due after the delay. No process has to stay alive
// until then.
using Acta;
using Acta.Learn.DelayedJob;
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

// learn: Schedule the job instead of enqueueing it
// ScheduleAsync counts the delay on the database clock (db_now + 5s); this host's clock does not affect timing.
var handle = await jobs.ScheduleAsync(new SendReminder("Sam"), TimeSpan.FromSeconds(5));

// learn: end
Console.WriteLine($"Job {handle.JobRef} enqueued at {DateTime.UtcNow:HH:mm:ss}; runs in ~5s.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.DelayedJob
{
    public sealed record SendReminder(string Name);

    public static class ReminderJob
    {
        [Job("send-reminder")]
        public static void Handle(SendReminder input) =>
            Console.WriteLine($"Reminder fired for {input.Name} at {DateTime.UtcNow:HH:mm:ss}.");
    }
}
