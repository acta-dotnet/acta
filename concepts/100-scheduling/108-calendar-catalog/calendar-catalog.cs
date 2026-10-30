// Concept: a calendar kept as data in the catalog. An operator publishes it, code binds it by name,
// and a later publication retimes every schedule bound to it in one transaction while workers run.
using System.Globalization;
using Acta;
using Acta.Concepts.CalendarCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A family in sys is shared: every namespace binds it by its plain name.
var officeHours = new CalendarLookup("sys", "office-hours");

// Phase 1: the calendar is published before the job that binds it deploys, as an operator would do it from
// the dashboard. An enqueue-only host (no Run<>) still bootstraps the schema, and the operations need no worker.
{
    var seed = Host.CreateApplicationBuilder(args);
    seed.Services.UseActa(j => j.UseLocalDatabase(seed.Configuration));
    using var seedHost = seed.Build();
    await seedHost.StartAsync();

    var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;
    var family = await calendars.GetAsync(officeHours);
    // A save names the latest version it saw, 0 for a new family; a publication names the current one.
    var latest = family?.Versions[0].Version ?? 0;
    var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OpenAllDay, latest, "Open all day");
    Console.WriteLine($"Draft saved: {draft.Action}, version {draft.Version}");
    var published = await calendars.PublishAsync(officeHours, family?.CurrentVersion ?? 0, draft.Version!.Value, reasonMessage: "Go live");
    Console.WriteLine($"Published: {published.Action}, version {published.Version} is current");

    await seedHost.StopAsync();
}

// Phase 2: the worker registers every-10s-in-hours against the catalog. Nothing in this process declares
// office-hours, so the name resolves to the shared family's current version, afresh at every evaluation.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CalendarCatalogJobs>("calendar-catalog");
});

using var host = builder.Build();
await host.StartAsync();

var operations = host.Services.GetRequiredService<IActaOperations>();
Console.WriteLine("Ticking every 10s inside the open window...");
await Task.Delay(TimeSpan.FromSeconds(25));

// The operator closes today. The draft is previewed beside the current version, then published: the bound
// schedule's cursor moves to the first instant inside the next window.
var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
var current = (await operations.Calendars.GetAsync(officeHours))!;
var closed = await operations.Calendars.SaveDraftAsync(
    officeHours,
    CalendarRoleCode.Availability,
    Definitions.ClosedOn(today),
    current.Versions[0].Version,
    $"Closed on {today}"
);
var preview = (await operations.Calendars.PreviewAsync(officeHours, closed.Version!.Value, count: 3))!;
Console.WriteLine(
    $"Preview of version {closed.Version}: the next window opens {preview.Windows[0].StartUtc:O}; "
        + $"{preview.Schedules.Count} bound schedule(s), {preview.Schedules.Count(s => s.Overdue)} overdue"
);

var result = await operations.Calendars.PublishAsync(
    officeHours,
    current.CurrentVersion!.Value,
    closed.Version.Value,
    reasonMessage: $"Closed on {today}"
);
Console.WriteLine($"Published: {result.Action}; retimed [{string.Join(", ", result.RetimedSchedules)}]");

// The namespace also lists the system jobs' schedules (alerts, retention), so the read names the job.
var schedule = (
    await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "calendar-catalog", JobName: "tick"))
).Items.Single();
Console.WriteLine($"Next occurrence {schedule.NextOccurrenceAtUtc:O}.");

Console.WriteLine("Watching 15s for a tick that must not come...");
await Task.Delay(TimeSpan.FromSeconds(15));
await host.StopAsync();

namespace Acta.Concepts.CalendarCatalog
{
    public readonly record struct Tick;

    public static class Definitions
    {
        public const string OpenAllDay = """
            {"kind":"weekly-availability","timeZone":"UTC",
             "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],"windows":[["00:00","23:59:59"]]}]}
            """;

        // A date override replaces the weekday's windows for that date; none closes it.
        public static string ClosedOn(string date) =>
            $$$"""
                {"kind":"weekly-availability","timeZone":"UTC",
                 "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],"windows":[["00:00","23:59:59"]]}],
                 "dateOverrides":{"{{{date}}}":[]}}
                """;
    }

    public sealed class TickJob
    {
        [Job("tick")]
        [Schedule("every-10s-in-hours", "PT10S", Availability = "office-hours")]
        public Task Handle(Tick input, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC tick");
            return Task.CompletedTask;
        }
    }
}
