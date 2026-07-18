// Concept: controlling a recurring schedule from outside the handler with ISchedules.
// Pause indefinitely, pause with a timed expiry, and resume; read status back after each step.
using Acta;
using Acta.Concepts.ScheduleControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScheduleControlJobs>("schedule-control");
});

using var host = builder.Build();
await host.StartAsync();

var schedules = host.Services.GetRequiredService<IJobs>().Schedules;
var queries = host.Services.GetRequiredService<IJobs>();

// The recurring slot registers on StartAsync; give the worker a moment to reconcile.
await Task.Delay(500);

var lookup = new JobScheduleLookup(JobLookup.ByDeduplicationKey("schedule-control", "hourly-report"), "every-hour");

// Step 1: indefinite pause - the schedule does not fire until an operator explicitly resumes it.
Console.WriteLine("Pausing schedule indefinitely...");
var r1 = await schedules.PauseAsync(lookup, untilUtc: null, note: "maintenance window");
Console.WriteLine($"After indefinite pause: status={r1.Status} next-run={r1.NextRunAtUtc?.ToString("O") ?? "(none)"}");

// Step 2: timed pause - the scheduler auto-resumes the schedule when the timestamp passes.
var resumeAt = DateTime.UtcNow.AddHours(2);
Console.WriteLine($"Replacing with timed pause until {resumeAt:O}...");
var r2 = await schedules.PauseAsync(lookup, untilUtc: resumeAt, note: "drain window");
Console.WriteLine($"After timed pause: status={r2.Status} paused-until={r2.PausedUntilUtc?.ToString("O") ?? "(none)"}");

// Step 3: resume - clears the pause, reconciles the cursor, recomputes the slot's next run.
Console.WriteLine("Resuming schedule...");
var r3 = await schedules.ResumeAsync(lookup, note: "drain complete");
Console.WriteLine($"After resume: status={r3.Status} next-run={r3.NextRunAtUtc?.ToString("O") ?? "(none)"}");

// Confirm via the query surface that the schedule is active again and has a next-run time.
var page = await queries.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: "schedule-control"));
var item = page.Items.FirstOrDefault(i => i.ScheduleName == "every-hour");
Console.WriteLine($"Query confirms: status={item?.Status} next-run={item?.NextRunAtUtc?.ToString("O") ?? "(none)"}");

await host.StopAsync();

namespace Acta.Concepts.ScheduleControl
{
    public readonly record struct GenerateHourlyReport;

    public sealed class HourlyReportJob
    {
        // 1h interval: will not fire during the short demo run, so only schedule-control is exercised.
        [Job("hourly-report", AuditLevel = JobAuditLevelCode.Off)]
        [JobSchedule("every-hour", "1h")]
        public async Task Handle(GenerateHourlyReport input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Console.WriteLine("hourly report generated");
        }
    }
}
