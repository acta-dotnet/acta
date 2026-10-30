// Concept: one permanent job keeps an external system synchronized. The schedule admits a firing every
// five seconds onto one durable slot; each firing syncs one bounded batch and returns; the watermark is
// the job's own durable variable, read at the top of every execution, so a recovered firing continues
// where the lost one stopped.
using Acta;
using Acta.Concepts.ContinuousSynchronization;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "continuous-sync";
const string jobName = "sync-orders";
const string scheduleName = "every-5s";
var labDatabase = Path.Combine(Path.GetTempPath(), "acta-continuous-sync.db");
var crashMarker = Path.Combine(Path.GetTempPath(), "acta-continuous-sync-crash.txt");
var outageMarker = Path.Combine(Path.GetTempPath(), "acta-continuous-sync-outage.txt");
var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "tour";

if (mode is not ("tour" or "worker" or "more" or "crash" or "outage" or "inspect" or "reset"))
{
    PrintCommands();
    return;
}

if (mode == "reset")
{
    foreach (var file in new[] { labDatabase, crashMarker, outageMarker })
    {
        File.Delete(file);
    }
    Console.WriteLine("The lab's database and markers are gone; the next run starts from nothing.");
    return;
}

if (mode == "crash")
{
    File.WriteAllText(crashMarker, DateTime.UtcNow.ToString("O"));
    Console.WriteLine("Armed: the worker kills its process after the next batch it commits.");
    return;
}

if (mode == "outage")
{
    if (File.Exists(outageMarker))
    {
        File.Delete(outageMarker);
        Console.WriteLine("The ERP feed is reachable again; the next retry succeeds.");
    }
    else
    {
        File.WriteAllText(outageMarker, DateTime.UtcNow.ToString("O"));
        Console.WriteLine(
            "The ERP feed is down: the next firing fails and retries under the job's budget. Run outage again to restore it."
        );
    }
    return;
}

// The mode word and the lab switches are not configuration; everything else still reaches the host.
var hostArgs = args.Where(a => a != mode && a is not "--brief" and not "--pause" and not "--all-columns").ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var provider = builder.Configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER") ?? "sqlite";
if (!LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine("This lab runs on embedded SQLite: its ERP feed and local orders live in the same file as Acta's tables.");
    Console.WriteLine("Unset ACTA_LOCAL_PROVIDER (or set it to sqlite) and run again.");
    return;
}

// One file of its own, so reset can start the story from nothing without touching another rung's database.
builder.Configuration["ConnectionStrings:acta"] ??= $"Data Source={labDatabase}";
var connectionString = LocalDatabase.ResolveConnectionString(builder.Configuration, provider);
var outage = new ErpOutage { IsActive = () => File.Exists(outageMarker) };
var erp = new ErpFeed(connectionString, outage);
var store = new OrderStore(connectionString);
await erp.EnsureCreatedAsync(default);
await store.EnsureCreatedAsync(default);

var lab = new ConceptLab(builder.Configuration, args);
builder.Services.AddSingleton(erp);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<CrashSignal>(new ProcessCrashSignal(crashMarker));
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    if (mode is "tour" or "worker")
    {
        j.Run<ContinuousSynchronizationJobs>(jobNamespace);
        // One knob shortens the whole coordination triple in proportion: a 1 s beat gives a 4 s lease and a 7 s
        // dead-worker window, so a restarted worker recovers a lost firing in seconds instead of minutes.
        j.ConfigureOptions(o => o.HeartbeatInterval = TimeSpan.FromSeconds(1));
    }
    else
    {
        j.Reference<ContinuousSynchronizationJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;

if (mode == "more")
{
    var count = args.Select(a => int.TryParse(a, out var n) ? n : 0).FirstOrDefault(n => n > 0);
    var head = await erp.AppendAsync(count == 0 ? 60 : count, default);
    Console.WriteLine($"The ERP published more changes; the feed now ends at change {head}.");
    await host.StopAsync();
    return;
}

if (mode == "inspect")
{
    await ShowStateAsync(lab, jobs, schedules, store, erp);
    await host.StopAsync();
    return;
}

if (await erp.HeadAsync(default) == 0)
{
    var seeded = await erp.AppendAsync(120, default);
    Console.WriteLine($"Seeded the ERP feed with {seeded} order changes; the local orders hold none yet.");
}

if (mode == "worker")
{
    Console.WriteLine(
        "The worker is running; the schedule fires every five seconds. In another terminal: more, crash, outage, inspect. Ctrl+C stops it."
    );
    await host.WaitForShutdownAsync();
    return;
}

Console.WriteLine("Watching three firings of one permanent job...");
var slotId = await SlotIdAsync(jobs, schedules);
using var tourTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
while ((await jobs.GetAsync(JobLookup.ById(slotId), tourTimeout.Token))?.ExecutionNumber < 3)
{
    await Task.Delay(250, tourTimeout.Token);
}

await ShowStateAsync(lab, jobs, schedules, store, erp);
await host.StopAsync();

static async Task<long> SlotIdAsync(IJobs jobs, ISchedules schedules)
{
    var schedule = (await schedules.ListAsync(new ListSchedulesQuery(JobNamespace: jobNamespace, JobName: jobName))).Items.Single(s =>
        s.ScheduleName == scheduleName
    );
    return (await jobs.GetJobIdAsync(JobLookup.ByRef(schedule.JobRef!.Value)))!.JobId;
}

static async Task ShowStateAsync(ConceptLab lab, IJobs jobs, ISchedules schedules, OrderStore store, ErpFeed erp)
{
    var slotId = await SlotIdAsync(jobs, schedules);
    await lab.ShowAllAsync(
        "Explore the complete permanent slot record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @slotId
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "One durable job identity, one row, Idle between firings",
        """
        SELECT job_ref, status, execution_number, failure_count, leased_by_worker_host_name, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @slotId
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "Acta's progress: the schedule cursor and when the engine next looks",
        """
        SELECT schedule_name, status, next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status
        FROM schedules_view
        WHERE namespace = @jobNamespace AND job_name = @jobName
        """,
        new { jobNamespace, jobName }
    );
    await lab.ShowAsync(
        "The firing envelope: which schedule admitted the running or last firing, and how it settled",
        """
        SELECT checkpoint_name, status, value_text
        FROM checkpoints_view
        WHERE job_id = @slotId AND kind = 'firing'
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The job's own progress: the watermark, a durable variable on the slot that every execution reads first",
        """
        SELECT checkpoint_name, kind, value_text, modified_at_utc
        FROM checkpoints_view
        WHERE job_id = @slotId AND kind = 'variable'
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The ledger: admissions, settlements, and a lost attempt reclaimed",
        """
        SELECT event_id, event, execution_number, event_message, details_text
        FROM events_view
        WHERE entity_kind = 'job' AND entity_id = @slotId
        ORDER BY event_id DESC LIMIT 8
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The lost attempt and what followed: the reclaim, the execution that finished its firing, and the next admission",
        """
        SELECT event_id, event, execution_number, event_message, details_text
        FROM events_view
        WHERE entity_kind = 'job' AND entity_id = @slotId AND event_id >= (
            SELECT COALESCE(MIN(event_id), 0)
            FROM events_view
            WHERE entity_kind = 'job' AND entity_id = @slotId AND event IN ('job.execution-orphaned', 'job.execution-failed')
        )
        ORDER BY event_id LIMIT 5
        """,
        new { slotId }
    );
    var (orders, lastChangeId) = await store.SummaryAsync(default);
    Console.WriteLine();
    Console.WriteLine("--- The destination against the feed ---");
    Console.WriteLine($"feed head: change {await erp.HeadAsync(default)}; local orders: {orders} (newest change {lastChangeId})");
}

static void PrintCommands()
{
    Console.WriteLine("dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- <mode>");
    Console.WriteLine("  tour     seed the feed, watch three firings, print the rows, exit (the default)");
    Console.WriteLine("  worker   run the worker until Ctrl+C, or until an armed crash");
    Console.WriteLine("  more [n] publish n more ERP changes (default 60)");
    Console.WriteLine("  crash    arm a process crash after the next committed batch");
    Console.WriteLine("  outage   switch the ERP feed off, or back on");
    Console.WriteLine("  inspect  print the slot, the cursor, the envelope, the watermark, and the ledger");
    Console.WriteLine("  reset    delete the lab's database and markers");
}
