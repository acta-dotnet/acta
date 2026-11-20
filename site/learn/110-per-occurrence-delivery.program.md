# The whole program: make every occurrence its own job

The how-to is [/learn/110-per-occurrence-delivery.md](/learn/110-per-occurrence-delivery.md). This file holds every source file of `learn/100-scheduling/110-per-occurrence-delivery` as it is compiled.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### per-occurrence-delivery.cs

```csharp
// How to make every occurrence its own job
// `ScheduledDelivery = PerOccurrence` makes each occurrence a root job of its own, with its own ref, retries, result,
// and retention. `MaxOutstanding` bounds how many may be unfinished at once.
using System.Collections.Concurrent;
using Acta;
using Acta.Labs;
using Acta.Learn.PerOccurrenceDelivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "per-occurrence";

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PerOccurrenceDeliveryJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();
var operations = host.Services.GetRequiredService<IActaOperations>();

// Each import holds until released, so the schedule fills: two roots run, and the occurrences behind them are skipped.
Console.WriteLine("Every second the schedule admits a root of its own; each import holds until released...");
using var fillTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (ImportJob.AtOnce < 2 || await CapacitySkipsAsync() == 0)
{
    await Task.Delay(100, fillTimeout.Token);
}
await ShowAsync(lab, "While full");

Console.WriteLine("Releasing the imports: the roots succeed, each settlement frees a place, and new roots are admitted...");
ImportJob.Release();
using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (ImportJob.Finished.Count < 4)
{
    await Task.Delay(100, drainTimeout.Token);
}
await operations.Schedules.PauseAsync(new ScheduleLookup(jobNamespace, "import-files", "every-second"), reasonMessage: "how-to done");
var skipped = await CapacitySkipsAsync();

Console.WriteLine();
Console.WriteLine("--- What the imports saw ---");
var refs = ImportJob.Finished.Distinct().Count();
Console.WriteLine($"{ImportJob.Finished.Count} imports finished, as {refs} different jobs, one per occurrence");
Console.WriteLine($"at most {ImportJob.MostAtOnce} were unfinished at once");
Console.WriteLine($"{skipped} occurrences were skipped while full, each audited as schedule.skipped with the reason capacity");
await ShowAsync(lab, "After the release");

await host.StopAsync();

// The occurrences the full schedule skipped, as the ledger audits them.
async Task<int> CapacitySkipsAsync()
{
    var skips = await operations.Ledger.ListEventsAsync(
        new ListEventsQuery(JobNamespace: jobNamespace, EventCode: EventCode.ScheduleSkipped, PageSize: 100)
    );
    return skips.Items.Count(e => e.DetailsText?.Contains("\"capacity\"", StringComparison.Ordinal) == true);
}

static async Task ShowAsync(LearnLab lab, string title)
{
    await lab.ShowAsync(
        $"{title}: one root job per occurrence, each with its own ref",
        """
        SELECT job_ref, job_status, created_at_utc
        FROM jobs_view
        WHERE namespace_name = @jobNamespace AND definition_name = 'import-files'
        ORDER BY job_id
        """,
        new { jobNamespace = "per-occurrence" }
    );
    await lab.ShowAsync(
        $"{title}: the schedule's capacity, MaxOutstanding = 2",
        """
        SELECT schedule_name, scheduled_delivery, max_outstanding, outstanding_count, full_behavior, evaluation_status
        FROM schedules_view
        WHERE namespace_name = @jobNamespace AND definition_name = 'import-files'
        """,
        new { jobNamespace = "per-occurrence" }
    );
    await lab.ShowAsync(
        $"{title}: occurrences skipped because the schedule was full",
        """
        SELECT event, details_text
        FROM events_view
        WHERE namespace_name = @jobNamespace AND definition_name = 'import-files' AND event = 'schedule.skipped'
        ORDER BY event_id DESC
        LIMIT 3
        """,
        new { jobNamespace = "per-occurrence" }
    );
}

namespace Acta.Learn.PerOccurrenceDelivery
{
    public sealed class ImportJob
    {
        private static readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly Lock Counting = new();
        private static int _atOnce;
        private static int _mostAtOnce;

        public static readonly ConcurrentQueue<JobRef> Finished = new();

        public static int AtOnce => Volatile.Read(ref _atOnce);

        public static int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public static void Release() => Gate.TrySetResult();

        // learn: Deliver each occurrence as a job of its own
        // Each occurrence is an ordinary root job with a ref, retries, and result of its own.
        // `MaxOutstanding` bounds how many may be unfinished at once, and a full schedule set to `Skip` passes over an
        // occurrence, audited as `schedule.skipped` with the reason `capacity`. Unset, `MaxOutstanding` is 1 and
        // `FullBehavior` is `Wait`, which holds an occurrence until one finishes. The body here only counts how
        // many run at once; a job of your own does the occurrence's work there.
        [Job(
            "import-files",
            ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence,
            EventRecording = EventRecordingCode.Audit,
            JobRetention = "1d"
        )]
        [Schedule("every-second", "PT1S", MaxOutstanding = 2, FullBehavior = ScheduleFullBehaviorCode.Skip)]
        // learn: end
        public async Task Import(JobContext ctx, CancellationToken ct)
        {
            lock (Counting)
            {
                _mostAtOnce = Math.Max(_mostAtOnce, ++_atOnce);
            }
            try
            {
                await Gate.Task.WaitAsync(ct);
                Finished.Enqueue(ctx.JobRef);
            }
            finally
            {
                lock (Counting)
                {
                    _atOnce--;
                }
            }
        }
    }
}
```

