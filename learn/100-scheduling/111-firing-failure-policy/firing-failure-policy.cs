// How to decide what a failed firing does to its schedule
// A firing that spends its retry budget returns the permanent job to `Idle`. `FiringFailurePolicy` says what happens
// to its schedules then: `Continue`, the default, fires the next occurrence as usual, and `PauseScheduling` pauses
// them, with the reason, until an operator resumes them.
using System.Collections.Concurrent;
using Acta;
using Acta.Labs;
using Acta.Learn.FiringFailurePolicy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string jobNamespace = "firing-failure";

var builder = Host.CreateApplicationBuilder(args);

// Every attempt here fails on purpose; keep the runtime's one warning per failed attempt out of the output.
builder.Logging.AddFilter("Acta.WorkerRuntime", LogLevel.Error);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FiringFailurePolicyJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();
var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;

Console.WriteLine("Two permanent jobs read the same price feed, which is down; each firing fails twice and spends its budget...");
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
    SELECT definition_name, schedule_status, pause_message, next_occurrence_at_utc
    FROM schedules_view
    WHERE namespace_name = @jobNamespace AND definition_name LIKE 'sync-prices-%'
    ORDER BY definition_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "The permanent jobs: both Idle between firings, whatever the policy did to the schedules",
    """
    SELECT definition_name, job_status, failure_count
    FROM jobs_view
    WHERE namespace_name = @jobNamespace AND definition_name LIKE 'sync-prices-%'
    ORDER BY definition_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "The ledger: two failed attempts, the second returning the job to Idle, and the pause the policy wrote with it",
    """
    SELECT definition_name, event, event_message, details_text
    FROM events_view
    WHERE namespace_name = @jobNamespace AND definition_name = 'sync-prices-pausing'
      AND event IN ('job.execution-failed', 'schedule.paused')
    ORDER BY event_id
    """,
    new { jobNamespace }
);

await schedules.PauseAsync(new ScheduleLookup(jobNamespace, "sync-prices-continuing", "every-second"), reasonMessage: "how-to done");
await host.StopAsync();

namespace Acta.Learn.FiringFailurePolicy
{
    public sealed class PriceFeedJobs
    {
        private static readonly ConcurrentDictionary<string, int> FailedAttempts = new();

        /// <summary>Firings of <paramref name="job"/> that spent their budget here: two failed attempts each.</summary>
        public static int SpentFirings(string job) => FailedAttempts.GetValueOrDefault(job) / 2;

        // learn: Pause the schedule when a firing fails for good
        // A firing retries under the job's budget like any attempt. The policy applies once that budget is spent; the
        // second job below keeps the default, `Continue`. `FailAsync` in the bodies is the program's own helper that
        // throws, so each attempt fails and retries; it is not `ctx.FailAsync`.
        [Job("sync-prices-pausing", MaxAttempts = 2, Backoff = "1s", FiringFailurePolicy = FiringFailurePolicyCode.PauseScheduling)]
        // learn: end
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
