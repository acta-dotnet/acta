// Concept: restarting a terminal job from outside the handler via IJobs.RestartAsync -
// resets the failure budget and retention, re-arms the same job id.
using Acta;
using Acta.Concepts.OperatorRestart;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

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
JobDetail? snapshot;
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

// RestartAsync re-arms the same job id: resets failure_count, clears retention, moves to Ready.
FlakyReportJob.DataSourceFixed = true;
var result = await jobs.RestartAsync(outcome, "operator re-ran the report after the data fix");
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
var jobId = outcome.JobId;
var operations = host.Services.GetRequiredService<IActaOperations>();
var events = await operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, EventCode: EventCode.JobRestarted));
foreach (var e in events.Items)
{
    Console.WriteLine($"event: {e.EventCode} actor={e.ActorCode} reason={e.ReasonCode} message=\"{e.ReasonMessage}\"");
}

await lab.ShowAsync(
    "Restart appends history; it does not manufacture exactly-once execution",
    """
    SELECT event, from_status, to_status, execution_number, reason
    FROM events_view
    WHERE job_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = outcome.JobId }
);

await host.StopAsync();

static async Task ShowRestartStateAsync(ConceptLab lab, string title, long jobId)
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
        SELECT job_id, job_ref, status, execution_number, failure_count, retention_until_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Completed step state is retained across operator restart",
        """
        SELECT step_name, state, attempt_number
        FROM steps_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Concepts.OperatorRestart
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
