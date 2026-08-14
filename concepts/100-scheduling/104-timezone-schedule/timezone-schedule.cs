using Acta;
using Acta.Concepts.TimezoneSchedule;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TimezoneScheduleJobs>("timezone-schedule");
});

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Two schedules on one job: the interval one fires every 15s so you see it work;");
Console.WriteLine("the zoned one fires 08:00 Ljubljana wall-clock, summer and winter alike. Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.TimezoneSchedule
{
    public readonly record struct DailyDigest;

    public sealed class DailyDigestJob
    {
        // TimeZoneId pins cron to wall-clock local time across DST switches (IANA or Windows ids; an
        // unresolvable id fails fast at worker startup). Intervals are absolute gaps and take no timezone.
        [Job("daily-digest")]
        [JobSchedule("morning-ljubljana", "0 8 * * *", TimeZoneId = "Europe/Ljubljana")]
        [JobSchedule("demo-tick", "PT15S")]
        public Task Handle(DailyDigest input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} fired by [{string.Join(", ", context.TriggeringScheduleNames)}]");
            return Task.CompletedTask;
        }
    }
}
