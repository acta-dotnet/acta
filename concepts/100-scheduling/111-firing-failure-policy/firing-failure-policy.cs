// Concept: what a firing that fails for good does to its schedules. A firing retries under the job's budget like any
// attempt; when the budget is spent the slot returns to Idle. Under the default Continue the next occurrence fires as
// usual; under PauseScheduling the definition's schedules pause, with the reason, until an operator resumes them.
using System.Collections.Concurrent;
using Acta;
using Acta.Concepts.FiringFailurePolicy;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string jobNamespace = "firing-failure";

var builder = Host.CreateApplicationBuilder(args);

// Every attempt here fails on purpose; keep the runtime's one warning per failed attempt out of the output.
builder.Logging.AddFilter("Acta.WorkerRuntime", LogLevel.Error);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FiringFailurePolicyJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();
var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;

Console.WriteLine("Two recurring jobs read the same price feed, which is down; each firing fails twice and spends its budget...");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
while (true)
{
    var listed = (await schedules.ListAsync(new ListSchedulesQuery(JobNamespace: jobNamespace), timeout.Token)).Items;
    var pausing = listed.SingleOrDefault(s => s.JobName == "sync-prices-pausing");
    if (pausing?.Status == ScheduleStatusCode.Paused && PriceFeedJobs.SpentFirings("sync-prices-continuing") >= 2)
    {
        break;
    }

    await Task.Delay(200, timeout.Token);
}

Console.WriteLine();
Console.WriteLine($"sync-prices-pausing     spent {PriceFeedJobs.SpentFirings("sync-prices-pausing")} firing, then its schedule paused");
Console.WriteLine($"sync-prices-continuing  spent {PriceFeedJobs.SpentFirings("sync-prices-continuing")} firings and is still scheduled");

await lab.ShowAsync(
    "The schedules: one paused with the policy's reason, one still active",
    """
    SELECT job_name, status, pause_message, next_occurrence_at_utc
    FROM schedules_view
    WHERE namespace = @jobNamespace AND job_name LIKE 'sync-prices-%'
    ORDER BY job_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "The slots: both Idle between firings, whatever the policy did to the schedules",
    """
    SELECT job_name, status, failure_count
    FROM jobs_view
    WHERE namespace = @jobNamespace AND job_name LIKE 'sync-prices-%'
    ORDER BY job_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "The ledger: two failed attempts, the second returning the slot to Idle, and the pause the policy wrote with it",
    """
    SELECT job_name, event, event_message, details_text
    FROM events_view
    WHERE namespace = @jobNamespace AND job_name = 'sync-prices-pausing'
      AND event IN ('job.execution-failed', 'schedule.paused')
    ORDER BY event_id
    """,
    new { jobNamespace }
);

await schedules.PauseAsync(new ScheduleLookup(jobNamespace, "sync-prices-continuing", "every-second"), reasonMessage: "concept done");
await host.StopAsync();

namespace Acta.Concepts.FiringFailurePolicy
{
    public sealed class PriceFeedJobs
    {
        private static readonly ConcurrentDictionary<string, int> FailedAttempts = new();

        /// <summary>Firings of <paramref name="job"/> that spent their budget here: two failed attempts each.</summary>
        public static int SpentFirings(string job) => FailedAttempts.GetValueOrDefault(job) / 2;

        [Job("sync-prices-pausing", MaxAttempts = 2, Backoff = "1s", FiringFailurePolicy = FiringFailurePolicyCode.PauseScheduling)]
        [Schedule("every-second", "PT1S")]
        public Task Pausing(CancellationToken ct) => FailAsync("sync-prices-pausing");

        [Job("sync-prices-continuing", MaxAttempts = 2, Backoff = "1s")]
        [Schedule("every-second", "PT1S")]
        public Task Continuing(CancellationToken ct) => FailAsync("sync-prices-continuing");

        private static Task FailAsync(string job)
        {
            FailedAttempts.AddOrUpdate(job, 1, (_, n) => n + 1);
            throw new InvalidOperationException("The price feed is down.");
        }
    }
}
