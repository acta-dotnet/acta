# The whole program: restart a failed job and keep its history

The how-to is [/learn/310-operator-restart.md](/learn/310-operator-restart.md). This file holds every source file of `learn/300-failure-and-recovery/310-operator-restart` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### operator-restart.cs

```csharp
// How to restart a failed job and keep its history
// `RestartAsync` runs a finished job again under the same identity. The failure budget is reset, the history is
// appended to, steps that succeeded are not run again, and the bare lines of the handler are.
using Acta;
using Acta.Labs;
using Acta.Learn.OperatorRestart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OperatorRestartJobs>("operator-restart");
});

// Three failures are deliberate course data; keep their events/rows without printing three framework stack traces.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Drive the job to terminal Failed by exhausting its retry budget.
Console.WriteLine("enqueueing flaky job (MaxAttempts=3, no backoff)...");
FlakyReportJob.DataSourceFixed = false;
var outcome = await jobs.EnqueueAsync(new FlakyReport("report-1"));

using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
GetJobResponse? snapshot;
while ((snapshot = await jobs.GetAsync(outcome, failureTimeout.Token))?.Status != JobStatusCode.Failed)
{
    if (snapshot?.Status.IsTerminal == true)
    {
        throw new InvalidOperationException($"Expected the fixture to fail, but it ended {snapshot.Status}.");
    }
    await Task.Delay(50, failureTimeout.Token);
}
Console.WriteLine($"terminal: status={snapshot!.Status} failureCount={snapshot.FailureCount} executionNumber={snapshot.ExecutionNumber}");
await ShowRestartStateAsync(lab, "Before restart: one stable identity exhausted its failure budget", outcome.JobId);

FlakyReportJob.DataSourceFixed = true;

// learn: Restart the job under the same identity
// RestartAsync re-arms the same job id: resets failure_count, clears retention, moves to Ready.
var result = await jobs.RestartAsync(outcome, "operator re-ran the report after the data fix");

// learn: end
Console.WriteLine($"restart result: {result.Action} status={result.Status}");

// The worker may already have claimed the row; failure_count is reset regardless of that scheduling race.
snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"immediately observed after restart: status={snapshot!.Status} failureCount={snapshot.FailureCount}");

using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((snapshot = await jobs.GetAsync(outcome, completionTimeout.Token))?.Status != JobStatusCode.Succeeded)
{
    if (snapshot?.Status.IsTerminal == true)
    {
        throw new InvalidOperationException($"Restarted job ended {snapshot.Status}; inspect its events for the reason.");
    }
    await Task.Delay(50, completionTimeout.Token);
}
Console.WriteLine($"re-run complete: status={snapshot!.Status} executionNumber={snapshot.ExecutionNumber}");
await ShowRestartStateAsync(lab, "After restart: same identity, reset budget, retained completed step", outcome.JobId);

// The restart reason is stamped on the job.restarted audit event.
var operations = host.Services.GetRequiredService<IActaOperations>();
var events = await operations.Ledger.ListEventsAsync(new ListEventsQuery(Job: outcome, EventCode: EventCode.JobRestarted));
foreach (var e in events.Items)
{
    Console.WriteLine($"event: {e.EventCode} message=\"{e.Message}\" details={e.DetailsText ?? "(none)"}");
}

await lab.ShowAsync(
    "Restart appends history; it does not manufacture exactly-once execution",
    """
    SELECT event, execution_number, event_message, details_text
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = outcome.JobId }
);

await host.StopAsync();

static async Task ShowRestartStateAsync(LearnLab lab, string title, long jobId)
{
    await lab.ShowAllAsync(
        $"Explore the complete job record: {title}",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        title,
        """
        SELECT job_id, job_ref, job_status, execution_number, failure_count, retention_until_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Completed step state is retained across operator restart",
        """
        SELECT step_name, step_status
        FROM steps_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Learn.OperatorRestart
{
    public sealed record FlakyReport(string Id);

    public static class FlakyReportJob
    {
        private static int _handlerEntries;

        public static bool DataSourceFixed { get; set; }

        // MaxAttempts=3, 0s skips backoff so the budget exhausts quickly.
        [Job("flaky-report", MaxAttempts = 3, Backoff = "0s")]
        public static async Task Handle(FlakyReport input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Id}] bare handler entry #{Interlocked.Increment(ref _handlerEntries)}");
            await context.RunStepAsync(
                "prepare-report",
                _ =>
                {
                    Console.WriteLine($"[{input.Id}] preparation step body invoked; its recorded success can be reused");
                    return Task.CompletedTask;
                },
                ct
            );

            if (!DataSourceFixed)
            {
                throw new InvalidOperationException($"[{input.Id}] data source unavailable");
            }
            Console.WriteLine($"[{input.Id}] report generated after operator restart");
        }
    }
}
```

## Notes

### The problem

An operator fixes an external dependency after a job exhausts its retry budget. They need to rerun the
same durable identity without erasing why it failed or accidentally assuming all handler code is safe.

### Common approaches

- Enqueue a brand-new job and correlate it manually.
- Increase automatic retries and wait.
- Repair the dependency, then restart the terminal identity.
- Perform the work manually outside the job system.

### Why this design

`RestartAsync` keeps `job_id`/`job_ref`, resets the failure budget and retention, increments later
execution numbers, and appends a restart event. Completed durable steps remain available to replay;
a failed at-most-once step runs again, while an interrupted one stays until the handler
reconciles it. A laned job can come back `Blocked` behind its lane's running job, and an `Executing` job
is refused.

### Trade-offs

Restart is not exactly-once recovery. Bare handler code and incomplete steps may execute again. Retaining
step state is useful only if step names and meanings remain compatible with the restarted job.

### Run the experiment

```bash
dotnet run --project learn/300-failure-and-recovery/310-operator-restart
```

The job fails three times, the lab marks its dependency repaired, and an operator restart succeeds.
Expected framework stack traces are suppressed so the four handler entries and durable state changes
remain readable; `events_view` retains the complete failure evidence.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before/after `jobs_view` proves stable identity, a higher execution number, and reset failure count.
`steps_view` keeps the successful preparation succeeded, so it does not run again; `events_view`
appends rather than rewrites history.

### Break it

Move the simulated side effect out of `RunStepAsync` and restart again. Its console output repeats. Then
interrupt a step before completion and compare with the at-most-once lab.

### When not to use

Enqueue a new identity when the business operation itself is new or needs separate retention/audit.
Avoid restart when handler evolution makes old payloads or durable names incompatible; migrate or repair
explicitly instead.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`operator-restart.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/300-failure-and-recovery/310-operator-restart/operator-restart.cs)
- [`RestartJob.sqlite.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Jobs/RestartJob/RestartJob.sqlite.sql)
