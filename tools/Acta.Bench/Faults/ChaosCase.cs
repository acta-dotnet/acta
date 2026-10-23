using System.Diagnostics;
using System.Globalization;
using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

/// <summary>
/// F09, the certification: seeds the crash-recovery mix, kills a worker every few seconds for the chaos window while
/// outbox rows stage into a producer database, quiesces, and reconciles every job against its model. The ledger checks
/// follow in <see cref="LedgerValidation"/>.
/// </summary>
internal static class ChaosCase
{
    private static readonly string[] Tenants = ["acme", "globex", "initech"];

    private sealed record CohortJob(string JobName, BenchWork Work, int? DelaySeconds = null, string? Lane = null, bool High = false);

    public static async Task<CaseResult> RunAsync(RunPlan plan, OwnedProvider environment, EvidenceBundle bundle, CancellationToken ct)
    {
        var tag = $"{environment.Provider}-F09-chaos";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        var source = new SqliteConnectionStringBuilder { DataSource = bundle.Resolve($"repro/state/{tag}/producer.db") }.ToString();
        await using (var producer = new SqliteConnection(source))
        {
            await producer.OpenAsync(ct);
            await using var create = producer.CreateCommand();
            create.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE domain_obligations(business_key TEXT PRIMARY KEY);"
                + SqliteOutboxDdl.CreateScript();
            await create.ExecuteNonQueryAsync(ct);
        }
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        foreach (var tenant in Tenants)
        {
            await host.Services.GetRequiredService<IActaOperations>().Tenants.RegisterAsync(tenant, tenant, ct: ct);
        }
        var heartbeat = (int?)plan.ProfileOverride("chaos_heartbeat_seconds");
        var executors = (int?)plan.ProfileOverride("chaos_executors");
        var workers = plan.ChaosWorkers(environment.Provider);
        var random = new Random(plan.ChaosSeed);
        List<OwnedWorker> owned = [];
        try
        {
            for (var i = 0; i < workers; i++)
            {
                owned.Add(Start());
            }
            await Task.WhenAll(owned.Select(w => w.ReadyAsync(ct)));
            var settings = owned[0].Settings!;
            // A killed worker's lease lapses within one lease, and the recovery pass that reclaims it runs once a lease.
            var warmup = (int)(plan.Policy("faults", "reclaim_lease_terms") * settings.LeaseSeconds);
            var chaos = (int)plan.ProfilePolicy("chaos_seconds");
            if (chaos <= warmup)
            {
                return CaseResult.Incomplete(
                    "F09",
                    environment.Provider,
                    "fault",
                    $"The {chaos}s chaos window ends inside the {warmup}s warm-up, so no reclaim could be observed."
                );
            }
            var cohort = Cohort(plan, warmup, chaos - warmup);
            bundle.Write(
                $"histories/{tag}-model.json",
                new Dictionary<string, string>
                {
                    ["seed"] = plan.ChaosSeed.ToString(CultureInfo.InvariantCulture),
                    ["shapes"] = string.Join(", ", cohort.GroupBy(s => s.JobName).Select(g => $"{g.Key} {g.Count()}")),
                    ["warmup_seconds"] = warmup.ToString(CultureInfo.InvariantCulture),
                    ["chaos_seconds"] = chaos.ToString(CultureInfo.InvariantCulture),
                    ["kill"] = "one live worker every kill interval for the chaos window, paused, its in-flight attempts read, then killed",
                    ["lease_timings"] = heartbeat is null ? "shipped" : "compressed",
                },
                BenchJson.Default.DictionaryStringString
            );
            var acknowledged = 0;
            foreach (var chunk in cohort.Chunk(1000))
            {
                var response = await jobs.EnqueueBatchAsync(
                    chunk
                        .Select(
                            (s, i) =>
                                new EnqueueJobRequest(
                                    "bench",
                                    s.JobName,
                                    JobPayload.Json(s.Work, WorkJson.Default.BenchWork),
                                    DeduplicationKey: s.Work.Key,
                                    Priority: s.High ? JobPriorityCode.High : null,
                                    DelaySeconds: s.DelaySeconds,
                                    // Half the jobs carry one of the tenants, so tenant and untenanted work run side by side.
                                    TenantName: i % (2 * Tenants.Length) < Tenants.Length ? Tenants[i % (2 * Tenants.Length)] : null,
                                    Lane: s.Lane
                                )
                        )
                        .ToArray(),
                    ct
                );
                acknowledged += response.Jobs.Count(j => j.Action == EnqueueJobAction.Inserted);
            }
            Console.WriteLine(
                $"  {tag}: seeded {acknowledged} jobs; {owned.Count} workers, lease {settings.LeaseSeconds}s, warm-up {warmup}s, chaos {chaos}s"
            );
            var outboxRows = (int)
                Math.Clamp(
                    plan.ChaosJobs * plan.Policy("faults", "chaos_mix", "outbox_fraction"),
                    plan.Policy("faults", "chaos_mix", "outbox_rows_min"),
                    plan.Policy("faults", "chaos_mix", "outbox_rows_cap")
                );
            var staging = StageAsync(source, outboxRows, (int)plan.Policy("faults", "chaos_mix", "outbox_chunk_rows"), chaos, ct);
            List<(long Id, int Execution)> victims = [];
            List<List<(long Id, int Execution)>> hits = [];
            var kills = 0;
            var refused = 0;
            var started = Stopwatch.StartNew();
            using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(plan.Policy("faults", "chaos_kill_interval_seconds"))))
            {
                while (started.Elapsed.TotalSeconds < chaos && await timer.WaitForNextTickAsync(ct))
                {
                    var running = owned.Where(w => w.IsRunning).ToArray();
                    var victim = running[random.Next(running.Length)];
                    // Paused, the victim's in-flight attempts are exactly those its kill interrupts.
                    victim.Pause();
                    List<(long Id, int Execution)> inFlight;
                    try
                    {
                        inFlight = await BusyAsync(state, victim.Id, ct);
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 15)
                    {
                        // SQLITE_PROTOCOL: the read gave up on the WAL index while the victim was paused, so its
                        // in-flight attempts are unknown; it resumes and a later tick kills.
                        victim.Resume();
                        Console.WriteLine(
                            $"  {tag}: {started.Elapsed.TotalSeconds:F0}s | busy probe refused (SQLITE_PROTOCOL), victim resumed"
                        );
                        continue;
                    }
                    if (!victim.Kill())
                    {
                        refused++;
                    }
                    kills++;
                    if (inFlight.Count > 0)
                    {
                        victims.AddRange(inFlight);
                        hits.Add(inFlight);
                    }
                    bundle.Append(
                        "faults.jsonl",
                        new FaultEvidence(
                            $"kill-{kills}",
                            inFlight.Select(v => v.Id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                            inFlight.Count > 0,
                            !victim.IsRunning,
                            false,
                            Provider: environment.Provider,
                            CaseId: "F09",
                            RecordedAtUtc: DateTimeOffset.UtcNow
                        ),
                        BenchJson.Default.FaultEvidence
                    );
                    var replacement = Start();
                    owned.Add(replacement);
                    await replacement.ReadyAsync(ct);
                    var elapsed = started.Elapsed.TotalSeconds;
                    Console.WriteLine(
                        $"  {tag}: {elapsed:F0}s | kill {kills} | busy {hits.Count} | interrupted attempts {victims.Count} | "
                            + (
                                elapsed < warmup
                                    ? $"warm-up, no reclaim possible for {warmup - elapsed:F0}s"
                                    : $"{chaos - elapsed:F0}s of chaos left"
                            )
                    );
                }
            }
            await staging;
            var producerCounts = await ProducerAsync(source, ct);
            var total = acknowledged + producerCounts.Operations;
            // Work held by killed workers stays Executing until its lease lapses and a recovery pass reclaims it.
            var bound = plan.Deadline(total, settings.LeaseSeconds, environment.DrainRate);
            var quiescing = Stopwatch.StartNew();
            bool quiesced;
            while (true)
            {
                producerCounts = await ProducerAsync(source, ct);
                var (all, done) = await TerminalAsync(state, ct);
                quiesced = producerCounts.Pending == 0 && all == total && done == all;
                if (quiesced || quiescing.Elapsed.TotalSeconds > bound)
                {
                    break;
                }
                if (quiescing.Elapsed.Seconds % 10 == 0)
                {
                    Console.WriteLine(
                        $"  {tag}: quiescing {quiescing.Elapsed.TotalSeconds:F0}s | terminal {done}/{total} | outbox pending {producerCounts.Pending}"
                    );
                }
                await Task.Delay(1000, ct);
            }
            var expected = WorkloadModel
                .Expected(cohort.Select(s => s.Work).Concat(producerCounts.Keys.Select(k => new BenchWork(k))))
                .Select(e => e.Key.StartsWith("charge-", StringComparison.Ordinal) ? e with { Status = "FailedOrSucceeded" } : e)
                .ToArray();
            var observed = await new DurableReader(state).OutcomesAsync(ct);
            var delivered = observed.Count(o => o.Key.StartsWith("outbox-", StringComparison.Ordinal));
            List<Check> checks = [.. OutcomeOracle.Reconcile(expected, observed)];
            checks.Add(
                new(
                    "outbox-drained",
                    "producer",
                    producerCounts.Pending == 0,
                    $"pending+claimed={producerCounts.Pending} quarantined={producerCounts.Quarantined}"
                )
            );
            checks.Add(
                new(
                    "outbox-delivered",
                    "producer",
                    delivered == producerCounts.Operations,
                    $"staged={producerCounts.Operations} ledger_jobs={delivered}"
                )
            );
            var rows = (await new DurableReader(state).ReadAsync(null, ct)).ToDictionary(r => r.JobId);
            var reclaimed = victims.Count(v => rows.TryGetValue(v.Id, out var row) && row.ExecutionNumber > v.Execution);
            var recoveredHits = hits.Count(hit => hit.Any(v => rows.TryGetValue(v.Id, out var row) && row.ExecutionNumber > v.Execution));
            bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
            var unresolved = observed
                .Where(o => o.Status is not ("Succeeded" or "Failed" or "Cancelled"))
                .Select(o => o.Key)
                .ToHashSet(StringComparer.Ordinal);
            var safe = OutcomeOracle.SafetyFailures(checks.Where(c => c.Code != "outbox-drained"), unresolved, _ => true).Length == 0;
            var live = quiesced && producerCounts.Pending == 0;
            var covered = refused == 0 && recoveredHits >= plan.ProfilePolicy("chaos_minimum_busy_kills");
            List<string> limits = [];
            if (heartbeat is not null)
            {
                limits.Add(
                    $"Compressed lease timings (heartbeat {settings.HeartbeatSeconds}s, lease {settings.LeaseSeconds}s): this run tests the harness and does not certify the shipped configuration."
                );
            }
            if (environment.Provider == "sqlite")
            {
                limits.Add(
                    $"SQLite fleet ({workers} workers): SQLite is single-node, so this run does not certify a distributed multi-worker deployment."
                );
            }
            return new(
                "F09",
                environment.Provider,
                "fault",
                0,
                !safe || !live ? "FAIL"
                    : !covered ? "INCONCLUSIVE"
                    : "PASS",
                !safe ? "Identity, outcome, or outbox delivery checks failed; exact identities retained."
                    : !live
                        ? $"The run did not quiesce: {unresolved.Count} jobs unresolved, {producerCounts.Pending} outbox rows undelivered."
                    : !covered ? "Required busy-worker kills with observed reclaim were not all established."
                    : limits.Count > 0 ? string.Join(" ", limits)
                    : null,
                safe ? "PASS" : "FAIL",
                live ? "PASS" : "FAIL",
                covered ? "COMPLETE" : "INCOMPLETE",
                "NOT_MEASURED",
                new()
                {
                    ["intended_roots"] = new(plan.ChaosJobs, "roots"),
                    ["acknowledged_roots"] = new(acknowledged, "roots"),
                    ["staged_outbox_operations"] = new(producerCounts.Operations, "operations"),
                    ["delivered_outbox_jobs"] = new(delivered, "jobs"),
                    ["worker_processes"] = new(workers, "processes"),
                    ["executors_per_worker"] = new(settings.Executors, "executors"),
                    ["worker_heartbeat"] = new(settings.HeartbeatSeconds, "seconds"),
                    ["worker_lease"] = new(settings.LeaseSeconds, "seconds"),
                    ["worker_dead_after"] = new(settings.DeadAfterSeconds, "seconds"),
                    ["warmup_seconds"] = new(warmup, "seconds"),
                    ["chaos_window_seconds"] = new(chaos, "seconds"),
                    ["kills"] = new(kills, "kills"),
                    ["observed_busy_kills"] = new(hits.Count, "kills"),
                    ["kills_with_reclaim"] = new(recoveredHits, "kills"),
                    ["interrupted_attempts"] = new(victims.Count, "attempts"),
                    ["reclaimed_attempts"] = new(reclaimed, "attempts"),
                },
                [
                    $"histories/{tag}-checks.json",
                    $"histories/{tag}-model.json",
                    .. bundle.WriteRows(tag, safe && live && covered, expected, observed),
                ]
            );
        }
        finally
        {
            foreach (var process in owned)
            {
                process.Dispose();
            }
        }

        OwnedWorker Start() => OwnedWorker.Start(state, outboxSource: source, executors: executors, heartbeatSeconds: heartbeat);
    }

    /// <summary>
    /// The crash-recovery mix: the protocol's shape fraction each of meters, laned jobs, and charges, the rest slow stepped
    /// jobs. The meters come due together at the warm-up's end and the lanes and charges spread over the rest of the
    /// chaos window, where a kill can interrupt them and its reclaim be observed.
    /// </summary>
    private static CohortJob[] Cohort(RunPlan plan, int warmup, int spread)
    {
        double Mix(string name) => plan.Policy("faults", "chaos_mix", name);
        var stepMs = (int)plan.ProfilePolicy("chaos_step_ms");
        var slice = (int)(plan.ChaosJobs * Mix("shape_fraction"));
        var laneDepth = (int)Mix("lane_depth");
        var metered = Math.Min(Math.Max(3, slice), (int)Mix("metered_roots_cap"));
        var laned = Math.Min(Math.Max(laneDepth, slice), (int)Mix("laned_roots_cap"));
        var lanes = Math.Max(1, laned / laneDepth);
        var charges = Math.Max(1, slice);
        var slow = Math.Max(0, plan.ChaosJobs - metered - laned - charges);
        List<CohortJob> cohort =
        [
            .. Enumerable
                .Range(0, slow)
                .Select(i => new CohortJob("slow-success", new($"slow-{i}", Steps: (int)Mix("slow_steps"), WorkMs: stepMs))),
        ];
        var meters = plan.FullPolicy.GetProperty("faults").GetProperty("chaos_mix").GetProperty("meter_shares").EnumerateObject().ToArray();
        var remaining = metered;
        for (var m = 0; m < meters.Length; m++)
        {
            var count = m == meters.Length - 1 ? remaining : (int)(metered * meters[m].Value.GetDouble());
            remaining -= count;
            cohort.AddRange(
                Enumerable
                    .Range(0, count)
                    .Select(i => new CohortJob(
                        meters[m].Name,
                        new($"{meters[m].Name}-{i}", WorkMs: (int)Mix("metered_dwell_ms")),
                        warmup,
                        High: true
                    ))
            );
        }
        // A lane's members are enqueued in the order the lane must run them, all due at the lane's instant.
        cohort.AddRange(
            Enumerable
                .Range(0, laned)
                .Select(i => new CohortJob(
                    "laned",
                    new($"laned-{i}", Steps: (int)Mix("lane_steps"), WorkMs: stepMs),
                    warmup + i % lanes % spread,
                    $"lane-{i % lanes}",
                    true
                ))
        );
        cohort.AddRange(
            Enumerable
                .Range(0, charges)
                .Select(i => new CohortJob(
                    "at-most-once-charge",
                    new($"charge-{i}", WorkMs: stepMs * (int)Mix("charge_work_steps"), EffectMode: "charge", AtMostOnce: true),
                    warmup + i % spread,
                    High: true
                ))
        );
        return [.. cohort];
    }

    /// <summary>
    /// Stages outbox rows in small committed batches spread over the chaos window, so relay claims race worker kills the
    /// whole time. Best effort: the delivered check compares against what actually committed.
    /// </summary>
    private static async Task StageAsync(string source, int total, int chunk, int seconds, CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(seconds / (double)(total / chunk + 1));
        try
        {
            for (var first = 0; first < total; first += chunk)
            {
                await using var connection = new SqliteConnection(source);
                await connection.OpenAsync(ct);
                await using var transaction = connection.BeginTransaction(deferred: false);
                for (var i = first; i < Math.Min(total, first + chunk); i++)
                {
                    var work = new BenchWork($"outbox-{i}");
                    await using var domain = connection.CreateCommand();
                    domain.Transaction = transaction;
                    domain.CommandText = "INSERT INTO domain_obligations VALUES(@key)";
                    domain.Parameters.AddWithValue("@key", work.Key);
                    await domain.ExecuteNonQueryAsync(ct);
                    await transaction.AddToActaOutboxAsync(
                        new EnqueueJobRequest(
                            "bench",
                            "work",
                            JobPayload.Json(work, WorkJson.Default.BenchWork),
                            DeduplicationKey: work.Key
                        ),
                        cancellationToken: ct
                    );
                }
                await transaction.CommitAsync(ct);
                await Task.Delay(pause, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  F09 outbox staging stopped early ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private static async Task<(long Pending, long Quarantined, int Operations, string[] Keys)> ProducerAsync(
        string source,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection(source);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT SUM(CASE WHEN status_code <> {(int)OutboxStatusCode.Quarantined} THEN 1 ELSE 0 END), SUM(CASE WHEN status_code = {(int)OutboxStatusCode.Quarantined} THEN 1 ELSE 0 END) FROM acta_outbox; SELECT business_key FROM domain_obligations ORDER BY business_key;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var pending = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var quarantined = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        await reader.NextResultAsync(ct);
        List<string> keys = [];
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }
        return (pending, quarantined, keys.Count, [.. keys]);
    }

    private static async Task<List<(long Id, int Execution)>> BusyAsync(OwnedProvider state, int pid, CancellationToken ct)
    {
        await using var connection = await state.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var prefix = state.Provider == "sqlite" ? "main" : state.Schema;
        command.CommandText =
            $"SELECT r.job_id, r.execution_number FROM {prefix}.jobs_state r JOIN {prefix}.workers w ON w.worker_id=r.leased_by_worker_id JOIN {prefix}.jobs j ON j.job_id=r.job_id JOIN {prefix}.definitions d ON d.definition_id=j.definition_id WHERE w.process_id={pid} AND r.job_status_code=50 AND d.definition_name NOT LIKE 'sys.%'";
        List<(long, int)> jobs = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(
                (
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)
                )
            );
        }
        return jobs;
    }

    /// <summary>How many of the run's jobs exist and how many are terminal; system jobs recur forever and are not the run's.</summary>
    private static async Task<(long All, long Terminal)> TerminalAsync(OwnedProvider state, CancellationToken ct)
    {
        await using var connection = await state.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var prefix = state.Provider == "sqlite" ? "main" : state.Schema;
        command.CommandText =
            $"SELECT COUNT(*), SUM(CASE WHEN r.job_status_code IN (100, 200, 220) THEN 1 ELSE 0 END) FROM {prefix}.jobs_state r JOIN {prefix}.jobs j ON j.job_id=r.job_id JOIN {prefix}.definitions d ON d.definition_id=j.definition_id WHERE d.definition_name NOT LIKE 'sys.%'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
        );
    }
}
