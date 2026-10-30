// Concept: a schedule can deliver each occurrence as a job of its own. A PerOccurrence definition's schedule admits a
// fresh root per occurrence, with its own ref, retries, result, and retention; MaxOutstanding bounds how many may be
// unfinished at once, and a full schedule set to Drop skips an occurrence with the reason "capacity".
using System.Collections.Concurrent;
using Acta;
using Acta.Concepts.PerOccurrenceDelivery;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "per-occurrence";

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PerOccurrenceDeliveryJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();
var operations = host.Services.GetRequiredService<IActaOperations>();

// Each import holds until released, so the schedule fills: two roots run, and the occurrences behind them are dropped.
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
await operations.Schedules.PauseAsync(new ScheduleLookup(jobNamespace, "import-files", "every-second"), reasonMessage: "concept done");
var dropped = await CapacitySkipsAsync();

Console.WriteLine();
Console.WriteLine("--- What the imports saw ---");
var refs = ImportJob.Finished.Distinct().Count();
Console.WriteLine($"{ImportJob.Finished.Count} imports finished, as {refs} different jobs, one per occurrence");
Console.WriteLine($"at most {ImportJob.MostAtOnce} were unfinished at once");
Console.WriteLine($"{dropped} occurrences were dropped while full, each audited as schedule.skipped with the reason capacity");
await ShowAsync(lab, "After the release");

await host.StopAsync();

// The occurrences the full schedule dropped, as the ledger audits them.
async Task<int> CapacitySkipsAsync()
{
    var skips = await operations.Ledger.ListEventsAsync(
        new ListEventsQuery(JobNamespace: jobNamespace, EventCode: EventCode.ScheduleSkipped, PageSize: 100)
    );
    return skips.Items.Count(e => e.DetailsText?.Contains("\"capacity\"", StringComparison.Ordinal) == true);
}

static async Task ShowAsync(ConceptLab lab, string title)
{
    await lab.ShowAsync(
        $"{title}: one root job per occurrence, each with its own ref",
        """
        SELECT job_ref, status, created_at_utc
        FROM jobs_view
        WHERE namespace = @jobNamespace AND job_name = 'import-files'
        ORDER BY job_id
        """,
        new { jobNamespace = "per-occurrence" }
    );
    await lab.ShowAsync(
        $"{title}: the schedule's capacity, MaxOutstanding = 2",
        """
        SELECT schedule_name, scheduled_delivery, max_outstanding, outstanding_count, full_behavior, evaluation_status
        FROM schedules_view
        WHERE namespace = @jobNamespace AND job_name = 'import-files'
        """,
        new { jobNamespace = "per-occurrence" }
    );
    await lab.ShowAsync(
        $"{title}: occurrences dropped because the schedule was full",
        """
        SELECT event, details_text
        FROM events_view
        WHERE namespace = @jobNamespace AND job_name = 'import-files' AND event = 'schedule.skipped'
        ORDER BY event_id DESC
        LIMIT 3
        """,
        new { jobNamespace = "per-occurrence" }
    );
}

namespace Acta.Concepts.PerOccurrenceDelivery
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

        // Each occurrence is an ordinary root job with a ref, retries, and result of its own.
        [Job("import-files", ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]
        [Schedule("every-second", "PT1S", MaxOutstanding = 2, FullBehavior = ScheduleFullBehaviorCode.Drop)]
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
