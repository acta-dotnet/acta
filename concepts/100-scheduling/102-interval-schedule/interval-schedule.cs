using Acta;
using Acta.Concepts.IntervalSchedule;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<IntervalScheduleJobs>("interval-schedule");
});

// The [JobSchedule] below recurs on a fixed interval, so there's nothing to enqueue.
await builder.Build().RunAsync();

namespace Acta.Concepts.IntervalSchedule
{
    public readonly record struct Heartbeat;

    public sealed class HeartbeatJob
    {
        // A schedule can be cron or an interval duration - the human form ("10s") or ISO 8601 ("PT10S",
        // and calendar forms like "P1D"). An interval has no wall-clock anchor, so it ignores TimeZone.
        [Job("heartbeat")]
        [JobSchedule("every-10-seconds", "10s")]
        public void Handle(Heartbeat input) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} heartbeat");
    }
}
