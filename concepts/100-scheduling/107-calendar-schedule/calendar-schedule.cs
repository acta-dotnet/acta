// Concept: schedules bound by name to calendars you write. An occurrence calendar supplies the
// instants a schedule fires; an availability calendar says when any schedule may fire at all.
using Acta;
using Acta.Concepts.CalendarSchedule;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CalendarScheduleJobs>("calendar-schedule");
});

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Two schedules on one job, each bound by name to a calendar declared in this file:");
Console.WriteLine("  on-the-calendar fires at :00, :20, and :40 of every minute, the instants third-minutes produces;");
Console.WriteLine("  every-5s-while-open ticks every 5s, but only inside first-half-minute's windows, [:00, :30) of each minute.");
Console.WriteLine("Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.CalendarSchedule
{
    public readonly record struct Tick;

    // An occurrence calendar answers the first instant strictly after the one asked for: UTC in, UTC out,
    // whole milliseconds, null once nothing remains. The engine passes the schedule's zone for calendars
    // that think in local time; this one does not need it.
    [Calendar("third-minutes")]
    public sealed class ThirdMinutesCalendar : IActaCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context)
        {
            var after = context.AfterUtc;
            var next = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, DateTimeKind.Utc);
            while (next <= after)
            {
                next = next.AddSeconds(20);
            }

            return next;
        }
    }

    // An availability calendar answers the window containing the instant, or the earliest later one, as
    // [StartUtc, EndUtc). A tick outside every window is omitted, never shifted into one.
    [Calendar("first-half-minute")]
    public sealed class FirstHalfMinuteCalendar : IActaAvailabilityCalendar
    {
        public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
        {
            var at = context.AtOrAfterUtc;
            var minute = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, DateTimeKind.Utc);
            var start = at < minute.AddSeconds(30) ? minute : minute.AddMinutes(1);
            return new CalendarWindow(start, start.AddSeconds(30));
        }
    }

    public sealed class TickJob
    {
        // A calendar-bound schedule has no expression: the calendar is the source. An availability binding
        // filters any source, here a five-second interval.
        [Job("tick")]
        [Schedule("on-the-calendar", Calendar = "third-minutes")]
        [Schedule("every-5s-while-open", "PT5S", Availability = "first-half-minute")]
        public Task Handle(Tick input, JobContext context, CancellationToken ct)
        {
            var names = string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName));
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{names}]");
            return Task.CompletedTask;
        }
    }
}
