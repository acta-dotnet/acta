// How to run a job on the last business day of each month
// An occurrence calendar is the list of instants a schedule fires at, where a cron cannot say them: the last business
// day of a month, minus the holidays you name. The schedule takes its occurrences from the calendar.
using System.Globalization;
using Acta;
using Acta.Learn.BusinessDaySchedule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A family in sys is shared: every namespace binds it by its plain name.
var settlementDays = new CalendarLookup("sys", "settlement-days");
var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");
string Local(DateTime utc) =>
    TimeZoneInfo
        .ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
        .ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

// Phase 1: a deploy step publishes the calendar before the jobs that name it start. An enqueue-only host (no Run<>)
// still bootstraps the schema, and the operations need no worker.
long current;
{
    var seed = Host.CreateApplicationBuilder(args);
    seed.Services.UseActa(j => j.UseLocalDatabase(seed.Configuration));
    using var seedHost = seed.Build();
    await seedHost.StartAsync();

    var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;

    // learn: 2. Preview the draft, then publish it
    // The preview answers the draft's next occurrences from any instant, in the zone you name. December 2027 ends on
    // a Friday the definition lists as a holiday, so that month settles a day earlier.
    var draft = await calendars.SaveDraftAsync(settlementDays, CalendarRoleCode.Occurrence, Definitions.SettlementDays);
    var october = new DateTimeOffset(2027, 10, 1, 0, 0, 0, TimeSpan.Zero);
    var preview = (await calendars.PreviewAsync(settlementDays, draft.Version!.Value, october, count: 3, timeZoneId: "Europe/Ljubljana"))!;
    foreach (var occurrence in preview.OccurrencesUtc)
    {
        Console.WriteLine($"settles {Local(occurrence)}");
    }

    await calendars.PublishAsync(settlementDays, draft.Version.Value, reasonMessage: "Settlement days");
    // learn: end
    current = draft.Version.Value;

    await seedHost.StopAsync();
}

// Phase 2: the start deploys settle, whose schedule names settlement-days. The deploy checks that the family has a
// published version.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<BusinessDayScheduleJobs>("settlement");
});

using var host = builder.Build();
await host.StartAsync();

// The schedule has no expression of its own: its next occurrence is the calendar's next instant. The namespace also
// lists the system jobs' schedules (alerts, retention), so the read names the job.
var operations = host.Services.GetRequiredService<IActaOperations>();
var schedule = (await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "settlement", JobName: "settle"))).Items.Single();
var next = (await operations.Calendars.PreviewAsync(settlementDays, current, count: 1, timeZoneId: "Europe/Ljubljana"))!.OccurrencesUtc[0];
Console.WriteLine(
    $"Next settlement {Local(schedule.NextOccurrenceAtUtc!.Value)}; the calendar's next instant: {schedule.NextOccurrenceAtUtc == next}"
);

await host.StopAsync();

namespace Acta.Learn.BusinessDaySchedule
{
    public readonly record struct Settle;

    public static class Definitions
    {
        // learn: 1. Write the rule as a calendar definition
        // A `business-day` definition picks the first or last business day of each week, month, quarter or year, at
        // a local time. Business days are Monday to Friday unless `businessDays` says otherwise, and a date in
        // `holidays` is not one.
        public const string SettlementDays = """
            {
              "kind": "business-day",
              "select": "last",
              "period": "month",
              "time": "16:00",
              "holidays": ["2027-12-31"]
            }
            """;
        // learn: end
    }

    public sealed class SettleJob
    {
        [Job("settle")]
        // learn: 3. Name the calendar on the schedule
        // `Calendar` takes the place of a cron or an interval, and `TimeZoneId` is the zone the calendar's local time
        // is read in.
        [Schedule("month-end", Calendar = "settlement-days", TimeZoneId = "Europe/Ljubljana")]
        // learn: end
        public Task Handle(Settle input, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd} settled the month");
            return Task.CompletedTask;
        }
    }
}
