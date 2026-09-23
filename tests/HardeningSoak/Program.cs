using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Acta;
using Anvil.Bench;
using Microsoft.Data.Sqlite;
using Npgsql;

if (args[0] == "purge-smoke")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var metrics = await new PurgeScenario().RunAsync(
        new CellParams(args[1], Jobs: 200, Executors: 4, ClaimBatch: 16, PayloadBytes: 0, Iterations: 1, Rows: 200),
        BenchIdentity.NewSchema(DateTime.UtcNow),
        new BenchConfig(SeedHistory: 50),
        timeout.Token
    );
    if (metrics.JobsObserved != 200 || metrics.Extra is not { } extra || extra["purgeRows"] < 50 || extra["remainingRows"] != 0)
    {
        throw new InvalidOperationException("The existing purge scenario did not complete and delete its eligible audit rows.");
    }
    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS existing PurgeScenario: {metrics.Extra["purgeRows"]} expired rows deleted, none remaining.");
    return;
}

if (args[0] == "inspect-sqlite")
{
    await using var inspection = new SqliteConnection(
        new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadOnly }.ToString()
    );
    await inspection.OpenAsync();
    var report = new Dictionary<string, object?> { ["Database"] = args[1], ["InspectedUtc"] = DateTimeOffset.UtcNow };
    foreach (
        var query in new Dictionary<string, string>
        {
            ["StatusCounts"] =
                "SELECT d.name, r.status_code, COUNT(*) AS count, MIN(r.modified_at_utc) AS oldest_update, MAX(r.modified_at_utc) AS newest_update, MIN(r.lease_expires_at_utc) AS earliest_lease, MAX(r.lease_expires_at_utc) AS latest_lease, MIN(r.execution_number) AS min_execution,MAX(r.execution_number) AS max_execution FROM runtimes r JOIN jobs j ON j.id=r.job_id JOIN definitions d ON d.id=j.definition_id GROUP BY d.name,r.status_code",
            ["RuntimeColumns"] = "PRAGMA table_info(runtimes)",
            ["Workers"] = "SELECT * FROM workers",
            ["LatestEvents"] =
                "SELECT id,event_code,created_at_utc,job_id,execution_number,worker_id,from_status_code,to_status_code,reason_code FROM events ORDER BY id DESC LIMIT 20",
            ["CompletionTimeline"] =
                "SELECT CAST(created_at_utc / 30000 AS INTEGER)*30000 AS bucket_utc_ms, COUNT(*) AS completions FROM events WHERE event_code=41 AND definition_id=(SELECT id FROM definitions WHERE name='bench-audit') GROUP BY bucket_utc_ms ORDER BY bucket_utc_ms",
            ["Results"] = "SELECT COUNT(*) AS count FROM results",
        }
    )
    {
        try
        {
            await using var command = inspection.CreateCommand();
            command.CommandText = query.Value;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var field = 0; field < reader.FieldCount; field++)
                {
                    row[reader.GetName(field)] = reader.IsDBNull(field) ? null : reader.GetValue(field);
                }
                rows.Add(row);
            }
            report[query.Key] = rows;
        }
        catch (Exception ex)
        {
            report[query.Key] = ex.Message;
        }
    }
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(args[2], json);
    Console.WriteLine($"Inspection saved to {args[2]}");
    return;
}

var provider = args[0];
var durationSeconds = int.Parse(args[1], CultureInfo.InvariantCulture);
var rate = int.Parse(args[2], CultureInfo.InvariantCulture);
var output = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (provider is not ("pg" or "sqlite") || durationSeconds < 30 || rate < 20 || rate % 20 != 0)
{
    throw new ArgumentException("Use pg|sqlite, at least 30 seconds, and a positive multiple of 20 jobs/second.");
}
var schema = BenchIdentity.NewSchema(DateTime.UtcNow);

// The run itself plus the drain budget and the settle time around it.
const int deadlinePaddingSeconds = 180;
const int executors = 4;
const int claimBatch = 16;
const int seededHistory = 10_000;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + deadlinePaddingSeconds));
var ct = deadline.Token;
await using var host = await BenchHost.StartAsync(
    new BenchHostOptions
    {
        Provider = provider,
        Schema = schema,
        Profile = ExecutionProfile.Buffered,
        Executors = executors,
        ClaimBatch = claimBatch,
        RegisterSystemJobs = true,
        JobEventsRetentionDays = 1,
        SeedHistory = seededHistory,
    },
    ct
);
var qualified = provider == "sqlite" ? "main" : schema;

// Every status read in this file is over the workload's own jobs, joined the same way.
var workloadJobs = JobsOf(BenchHost.AuditJobName);
string JobsOf(string definitionName) =>
    $"{qualified}.runtimes r JOIN {qualified}.jobs j ON j.id=r.job_id JOIN {qualified}.definitions d ON d.id=j.definition_id WHERE d.name='{definitionName}'";
var maintenanceSetting = await ScalarAsync(provider == "pg" ? "SHOW autovacuum" : "PRAGMA journal_mode", ct);
if (Convert.ToString(maintenanceSetting, CultureInfo.InvariantCulture) != (provider == "pg" ? "on" : "wal"))
{
    throw new InvalidOperationException("Database maintenance was not enabled as expected.");
}
var beginning = await CountsAsync(ct);
var runtimeVersion = await ScalarAsync($"SELECT engine_version FROM {qualified}.workers LIMIT 1", ct);
await File.WriteAllTextAsync(
    output + ".run.json",
    JsonSerializer.Serialize(
        new
        {
            Provider = provider,
            Schema = schema,
            RuntimeVersion = runtimeVersion,
            StartedUtc = DateTimeOffset.UtcNow,
            Database = provider == "sqlite" ? new SqliteConnectionStringBuilder(ProviderConn.Resolve(provider, schema)).DataSource : schema,
            DurationSeconds = durationSeconds,
            TargetRatePerSecond = rate,
            OverallDeadlineSeconds = durationSeconds + deadlinePaddingSeconds,
        },
        new JsonSerializerOptions { WriteIndented = true }
    ),
    ct
);
var samples = new ConcurrentQueue<(double AtSeconds, double OldestReadySeconds, long Unfinished)>();
var durableProbeTicks = new ConcurrentQueue<long>();
var probes = new List<Task>();
var retentionRows = new ConcurrentQueue<int>();
var retentionPasses = 0;
var elapsed = Stopwatch.StartNew();
using var backgroundStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
var monitor = MonitorAsync(monitorStop.Token);
var retention = RetentionAsync(backgroundStop.Token);
var emitted = 0;
var tick = 0;
var perTick = Math.Max(1, rate / 20);
var pad = new string('x', 256);
while (elapsed.Elapsed.TotalSeconds < durationSeconds)
{
    if (monitor.IsFaulted)
    {
        await monitor;
    }
    if (retention.IsFaulted)
    {
        await retention;
    }
    var submitted = Stopwatch.GetTimestamp();
    var requests = Enumerable
        .Range(0, perTick)
        .Select(_ => new JobEnqueueRequest(
            BenchHost.Namespace,
            BenchHost.AuditJobName,
            JobPayload.Json(new BenchInput(submitted, pad, WorkMs: 1))
        ))
        .ToArray();
    var added = await host.Jobs.EnqueueBatchAsync(requests, ct);
    emitted += added.Count;
    if (tick % 20 == 0)
    {
        probes.Add(ObserveDurableCompletionAsync(added[^1], submitted, ct));
    }
    tick++;
    var sleep = tick * 50 - elapsed.ElapsedMilliseconds;
    if (sleep > 0)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(sleep), ct);
    }
}
var drain = Stopwatch.StartNew();
await backgroundStop.CancelAsync();
await retention;
while ((await StatusCountsAsync(ct)).Succeeded != emitted)
{
    if (drain.Elapsed > TimeSpan.FromSeconds(120))
    {
        throw new InvalidOperationException("The workload did not durably drain within 120 seconds.");
    }
    await Task.Delay(50, ct);
}
await Task.WhenAll(probes);
await monitorStop.CancelAsync();
await monitor;
var final = await CountsAsync(ct);

// A run that swept nothing observed nothing about retention under load, whatever else it did.
if (retentionPasses == 0)
{
    throw new InvalidOperationException("No retention pass completed during the run; lengthen it or check the sweep.");
}
if (host.Sink.Samples.Count != emitted || final.WorkloadFailed != 0 || final.Results != beginning.Results + emitted)
{
    throw new InvalidOperationException("Missing, duplicated, failed, or unretained workload results.");
}
var pickupTicks = host.Sink.Samples.OrderBy(s => s.Enqueued).Select(s => s.Entry - s.Enqueued).ToArray();
var pickup = Stats.Percentiles(pickupTicks.ToArray());
var durable = Stats.Percentiles(durableProbeTicks.ToArray());
var decile = Math.Max(1, pickupTicks.Length / 10);
var first = Stats.Percentiles(pickupTicks.Take(decile).ToArray());
var last = Stats.Percentiles(pickupTicks.TakeLast(decile).ToArray());
var maintenance =
    provider == "pg"
        ? await ScalarAsync($"SELECT COALESCE(sum(autovacuum_count),0)::bigint FROM pg_stat_user_tables WHERE schemaname = '{schema}'", ct)
        : await ScalarAsync("PRAGMA wal_autocheckpoint", ct);
var evidence = new
{
    Provider = provider,
    Profile = "Buffered",
    Schema = schema,
    RuntimeVersion = runtimeVersion,
    DurationSeconds = durationSeconds,
    TargetRatePerSecond = rate,
    ActualRatePerSecond = emitted / (double)durationSeconds,
    Executors = executors,
    ClaimBatch = claimBatch,
    SeededHistory = seededHistory,
    Emitted = emitted,
    HandlerEntries = host.Sink.Samples.Count,
    DurableSucceeded = final.WorkloadSucceeded,
    WorkloadFailed = final.WorkloadFailed,
    ResultsBefore = beginning.Results,
    ResultsAfter = final.Results,
    PickupP50Ms = pickup.P50,
    PickupP95Ms = pickup.P95,
    PickupP99Ms = pickup.P99,
    PickupMaxMs = pickup.Max,
    FirstDecilePickupP95Ms = first.P95,
    LastDecilePickupP95Ms = last.P95,
    DurableCompletionProbeCount = durableProbeTicks.Count,
    DurableCompletionP95Ms = durable.P95,
    DurableCompletionP99Ms = durable.P99,
    DurableCompletionMaxMs = durable.Max,
    PeakSampledOldestReadySeconds = samples.Max(s => s.OldestReadySeconds),
    PeakSampledUnfinishedJobs = samples.Max(s => s.Unfinished),
    RetentionPasses = retentionPasses,
    ExpiredEventRowsPresentedToRetention = retentionRows.Sum(),
    MaintenanceSetting = maintenanceSetting,
    AutovacuumCountOrWalAutoCheckpointPages = maintenance,
    FinalDrainSeconds = drain.Elapsed.TotalSeconds,
    Samples = samples
        .Select(s => new
        {
            s.AtSeconds,
            s.OldestReadySeconds,
            s.Unfinished,
        })
        .ToArray(),
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
Console.WriteLine(
    $"PASS {provider} Buffered: {emitted} durable completions; pickup p95/p99 {pickup.P95:F2}/{pickup.P99:F2}ms; durable probes p95/p99 {durable.P95:F2}/{durable.P99:F2}ms; {retentionPasses} retention passes. {output}"
);

async Task MonitorAsync(CancellationToken token)
{
    var now = provider == "pg" ? "EXTRACT(EPOCH FROM clock_timestamp())" : "(julianday('now')-2440587.5)*86400";
    var due = provider == "pg" ? "EXTRACT(EPOCH FROM r.next_run_at_utc)" : "r.next_run_at_utc / 1000.0";
    var oldestDueReady =
        $"SELECT COALESCE(MAX({now} - {due}),0) FROM {workloadJobs} AND r.status_code={(byte)JobStatusCode.Ready} AND {due} <= {now}";
    try
    {
        while (true)
        {
            var oldest = Convert.ToDouble(await ScalarAsync(oldestDueReady, token), CultureInfo.InvariantCulture);
            var counts = await StatusCountsAsync(token);
            samples.Enqueue((elapsed.Elapsed.TotalSeconds, oldest, counts.Unfinished));
            var progress = new
            {
                AtSeconds = elapsed.Elapsed.TotalSeconds,
                OldestReadySeconds = oldest,
                counts.Unfinished,
                counts.Succeeded,
                counts.Ready,
                counts.Dispatched,
                counts.Executing,
                LastWorkerSeen = await ScalarAsync($"SELECT MAX(last_seen_at_utc) FROM {qualified}.workers", token),
                RecoveryExecution = await ScalarAsync($"SELECT MAX(r.execution_number) FROM {JobsOf(BenchHost.RecoveryJobName)}", token),
                DeadlineCanceled = ct.IsCancellationRequested,
                ProducerFinished = elapsed.Elapsed.TotalSeconds >= durationSeconds,
            };
            await File.AppendAllTextAsync(output + ".progress.ndjson", JsonSerializer.Serialize(progress) + Environment.NewLine, token);
            await Task.Delay(1000, token);
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
}

async Task RetentionAsync(CancellationToken token)
{
    try
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            await host.AgeAllEventsAsync(days: 2, token);
            var expired = await host.CountExpiredEventsAsync(olderThanDays: 1, token);
            await host.TriggerSlotAsync(BenchHost.RetentionJobName, token);
            if (await host.CountExpiredEventsAsync(olderThanDays: 1, token) != 0)
            {
                throw new InvalidOperationException("A completed retention pass left expired events behind.");
            }
            retentionRows.Enqueue(expired);
            retentionPasses++;
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
}

async Task ObserveDurableCompletionAsync(JobEnqueueOutcome job, long submitted, CancellationToken token)
{
    while (await host.Jobs.GetStatusAsync(job, token) != JobStatusCode.Succeeded)
    {
        await Task.Delay(20, token);
    }
    if (await host.Jobs.GetResultAsync<BenchResultPayload>(job, token) is not { Ok: 1 })
    {
        throw new InvalidOperationException("A result reader lost durable output during retention.");
    }
    durableProbeTicks.Enqueue(Stopwatch.GetTimestamp() - submitted);
}

async Task<(long WorkloadSucceeded, long WorkloadFailed, long Results)> CountsAsync(CancellationToken token)
{
    var counts = await StatusCountsAsync(token);
    var results = Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {qualified}.results", token), CultureInfo.InvariantCulture);
    return (counts.Succeeded, counts.Failed, results);
}

// One round trip for every status the run reports, so a sample is one consistent read of the workload.
async Task<StatusCounts> StatusCountsAsync(CancellationToken token)
{
    var byStatus = new Dictionary<byte, long>();
    await using var connection = Open();
    await connection.OpenAsync(token);
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT r.status_code, COUNT(*) FROM {workloadJobs} GROUP BY r.status_code";
    command.CommandTimeout = 30;
    await using var reader = await command.ExecuteReaderAsync(token);
    while (await reader.ReadAsync(token))
    {
        byStatus[Convert.ToByte(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToInt64(
            reader.GetValue(1),
            CultureInfo.InvariantCulture
        );
    }
    long Of(JobStatusCode status) => byStatus.GetValueOrDefault((byte)status);
    var succeeded = Of(JobStatusCode.Succeeded);
    return new StatusCounts(
        succeeded,
        Of(JobStatusCode.Failed),
        Of(JobStatusCode.Ready),
        Of(JobStatusCode.Dispatched),
        Of(JobStatusCode.Executing),
        byStatus.Values.Sum() - succeeded
    );
}

async Task<object?> ScalarAsync(string sql, CancellationToken token)
{
    await using var connection = Open();
    await connection.OpenAsync(token);
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    return await command.ExecuteScalarAsync(token);
}

DbConnection Open() =>
    provider == "pg"
        ? new NpgsqlConnection(ProviderConn.Resolve(provider, schema))
        : new SqliteConnection(ProviderConn.Resolve(provider, schema));

internal sealed record StatusCounts(long Succeeded, long Failed, long Ready, long Dispatched, long Executing, long Unfinished);
