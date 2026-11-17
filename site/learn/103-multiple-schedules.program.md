# The whole program: give one job several schedules

The how-to is [/learn/103-multiple-schedules.md](/learn/103-multiple-schedules.md). This file holds every source file of `concepts/100-scheduling/103-multiple-schedules` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### multiple-schedules.cs

```csharp
// How to give one job several schedules
// Several `[Schedule]` attributes on one job share one permanent job, and schedules that come due together fire it
// once.
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

Console.WriteLine("Watching two schedule cursors drive one permanent job for 12 seconds...");
await Task.Delay(TimeSpan.FromSeconds(12));

await lab.ShowAllAsync(
    "Explore the complete reusable job record",
    """
    SELECT *
    FROM jobs_view
    WHERE namespace_name = @jobNamespace AND definition_name = @jobName
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "One permanent job identity, several executions",
    """
    SELECT job_id, job_ref, definition_name, job_status, execution_number
    FROM jobs_view
    WHERE namespace_name = @jobNamespace AND definition_name = @jobName
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "Two independently moving schedule cursors",
    """
    SELECT schedule_name, schedule_status, expression, next_occurrence_at_utc
    FROM schedules_view
    WHERE namespace_name = @jobNamespace AND definition_name = @jobName
    ORDER BY schedule_name
    """,
    new { jobNamespace = "multiple-schedules", jobName = "report" }
);
await lab.ShowAsync(
    "Occurrence history is append-only events, not new job identities",
    """
    SELECT event, execution_number, created_at_utc
    FROM events_view
    WHERE namespace_name = @jobNamespace AND definition_name = @jobName
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
        [Job("report")]
        // learn: 1. Put several schedules on the one job
        // Several [Schedule]s share one permanent job; schedules due at the same instant coalesce
        // into ONE execution, and context.TriggeringSchedules reports which fired.
        [Schedule("fast", "PT10S")]
        [Schedule("faster", "PT5S")]
        // learn: end
        public Task Handle(Report input, JobContext context, CancellationToken ct)
        {
            // learn: 2. Read which schedules fired this execution
            Console.WriteLine(
                $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
            );
            // learn: end
            return Task.CompletedTask;
        }
    }
}
```

## Notes

### The problem

Row-per-occurrence schedulers make every tick a new durable identity. That preserves independent
history, but it also creates unbounded job rows and more duplicate-fire edges.

### Common approaches

- Insert one job for every occurrence.
- Keep one permanent job and advance schedule cursors.
- Let an external scheduler enqueue one-shot work.
- Use an in-process timer and accept process-local availability.

### Why this design

Acta stores one reusable job identity and one cursor per schedule. A due schedule admits a firing onto the
same permanent job; schedules due together coalesce into one firing, whose `TriggeringSchedules` names what fired.

### Trade-offs

Occurrence history lives in the event ledger rather than in a new job identity, as far as `EventRecording`
keeps it; the job itself stores only its latest successful occurrence's result. Each schedule
still needs its own cursor, and coalescing is intentionally not a record of every missed business event.

### Run the experiment

```bash
dotnet run --project concepts/100-scheduling/103-multiple-schedules
```

The twelve-second run lets the five- and ten-second cursors move before printing the proof.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Expect one `jobs_view` row, two `schedules_view` rows, and several `events_view` executions. The view
queries are operator/learning surfaces; code should normally use `IJobs` and
`IActaOperations.Schedules`.

### Break it

Stop the process for more than a minute, restart it, and inspect how the misfire policy advances the
cursors (this lab declares none, so the default `Skip` applies; a cursor less than a minute overdue at a
start still fires). Lab [`106-schedule-misfire`](/learn/106-schedule-misfire.md) isolates the `Skip` versus
`CatchUpOnce` decision.

### When not to use

Use one-shot jobs when each financial/accounting occurrence needs an independent identity, retention
period, deduplication key, or audit outcome. Use an external scheduler when scheduling authority must sit
outside the Acta database.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`multiple-schedules.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/100-scheduling/103-multiple-schedules/multiple-schedules.cs)
- [`ScheduleAdmitter.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Scheduling/ScheduleAdmitter.cs)
- [`SchedulesView.sqlite.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Schema/Views/SchedulesView.sqlite.sql)
