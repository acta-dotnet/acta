using Acta;
using Acta.Concepts.DurableStep;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableStepJobs>("durable-step");
});

// Handler fails once to show retry; quiet the framework failure warning/alert.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var enqueued = await jobs.EnqueueAsync(new FreezeBox("box-1"));

JobDetail? snapshot;
using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
do
{
    await Task.Delay(100, completionTimeout.Token);
    snapshot = await jobs.GetAsync(enqueued, completionTimeout.Token);
} while (snapshot is null or { Status.IsTerminal: false });

await lab.ShowAllAsync(
    "Explore the complete replayed job record",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "One job replayed; the recorded step outcome was reused",
    """
    SELECT job_ref, status, execution_number, failure_count
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "The durable step slot stores the completed outcome",
    """
    SELECT step_name, state, attempt_number, reason
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "The event ledger keeps both executions",
    """
    SELECT event, from_status, to_status, execution_number, reason
    FROM events_view
    WHERE job_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = enqueued.JobId }
);

await host.StopAsync();

namespace Acta.Concepts.DurableStep
{
    public sealed record FreezeBox(string BoxId);

    public sealed class FreezeBoxJob
    {
        private static int _attempts;

        // MaxAttempts = 2: first attempt fails, second succeeds; retry waits 2s.
        [Job("freeze-box", MaxAttempts = 2, Backoff = "2s")]
        public async Task Handle(FreezeBox box, JobContext context, CancellationToken ct)
        {
            // In this controlled path the later failure happens after step success is durable, so execution two reuses that outcome.
            // A process crash after the external request but before Acta records success could invoke a normal step body again.
            await context.RunStepAsync(
                "start-freeze-cycle",
                _ =>
                {
                    Console.WriteLine($"[{box.BoxId}] requesting START-FREEZE with stable deduplication key freeze:{box.BoxId}");
                    return Task.CompletedTask;
                },
                ct: ct
            );

            // Fail the first attempt; the retry skips the already-started freeze cycle.
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                Console.WriteLine($"[{box.BoxId}] thermostat hiccup - retrying...");
                throw new InvalidOperationException("simulated transient failure");
            }

            Console.WriteLine($"[{box.BoxId}] box frozen");
        }
    }
}
