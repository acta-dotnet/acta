// How to replace a timer loop with an interval schedule
// An interval schedule, such as `10s`, runs a job at a fixed gap, where a timer loop in a hosted service would
// otherwise stand.
using Acta;
using Acta.Concepts.IntervalSchedule;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<IntervalScheduleJobs>("interval-schedule");
});

// The [Schedule] below recurs on a fixed interval, so there's nothing to enqueue.
await builder.Build().RunAsync();

namespace Acta.Concepts.IntervalSchedule
{
    public readonly record struct Heartbeat;

    public sealed class HeartbeatJob
    {
        [Job("heartbeat")]
        // learn: Give the schedule an interval instead of a cron
        // A schedule can be cron or an interval duration - the human form ("10s") or ISO 8601 ("PT10S",
        // and calendar forms like "P1D"). An interval has no wall-clock anchor, so it ignores TimeZoneId.
        [Schedule("every-10-seconds", "10s")]
        // learn: end
        public void Handle(Heartbeat input) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC heartbeat");
    }
}
