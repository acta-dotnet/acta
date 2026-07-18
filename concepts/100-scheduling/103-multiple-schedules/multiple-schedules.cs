using Acta;
using Acta.Concepts.MultipleSchedules;
using Acta.Labs;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<MultipleSchedulesJobs>("multiple-schedules");
});

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Watching two schedule cursors drive one reusable job slot for 12 seconds...");
await Task.Delay(TimeSpan.FromSeconds(12));

await lab.ShowAllAsync(
    "Explore the complete reusable job record",
    """
    SELECT *
    FROM jobs_view
    WHERE namespace = @jobNamespace AND job_name = @jobName
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "One recurring job identity, several executions",
    """
    SELECT job_id, job_ref, job_name, status, execution_number
    FROM jobs_view
    WHERE namespace = @jobNamespace AND job_name = @jobName
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "Two independently moving schedule cursors",
    """
    SELECT schedule_name, status, expression, next_run_at_utc
    FROM schedules_view
    WHERE namespace = @jobNamespace AND job_name = @jobName
    ORDER BY schedule_name
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "Occurrence history is append-only events, not new job identities",
    """
    SELECT event, execution_number, created_at_utc
    FROM events_view
    WHERE namespace = @jobNamespace AND job_name = @jobName
    ORDER BY event_id
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);

await host.StopAsync();

namespace Acta.Concepts.MultipleSchedules
{
    public readonly record struct Report;

    public sealed class ReportJob
    {
        // Several [JobSchedule]s share one recurring slot; schedules due at the same instant coalesce
        // into ONE execution, and ctx.TriggeringScheduleNames reports which fired.
        [Job("report")]
        [JobSchedule("fast", "PT10S")]
        [JobSchedule("faster", "PT5S")]
        public Task Handle(Report input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} fired by [{string.Join(", ", ctx.TriggeringScheduleNames)}]");
            return Task.CompletedTask;
        }
    }
}
