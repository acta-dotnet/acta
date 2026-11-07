using Acta;
using Acta.Concepts.Recurring;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RecurringJobs>("recurring-job");
});

// No enqueue: the deploy creates one permanent job for the [Schedule] below, and it fires on its cron cadence.
await builder.Build().RunAsync();

namespace Acta.Concepts.Recurring
{
    public readonly record struct PurgeStaleSessions;

    public sealed class PurgeStaleSessionsJob
    {
        // One permanent job fires on the cadence, and each successful fire's count replaces the last one stored;
        // EventRecordingCode.Failures writes an event only for a fire that fails or recovers.
        [Job("purge-stale-sessions", EventRecording = EventRecordingCode.Failures)]
        [Schedule("every-15-seconds", Cron.Every15Seconds)]
        public async Task<int> Handle(PurgeStaleSessions input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            var purged = Random.Shared.Next(0, 25);
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss} [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}] purged {purged} stale sessions"
            );
            return purged;
        }
    }
}
