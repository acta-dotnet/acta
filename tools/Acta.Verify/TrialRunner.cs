using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

/// <summary>
/// A case's trial. A measured phase runs as a <see cref="LoadTrial"/>; a verification trial submits a few jobs, waits for
/// every one, and reconciles each whole tree, children and checkpoints included, against its model.
/// </summary>
internal static class TrialRunner
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        string phase,
        int repetition,
        int offeredRate,
        CancellationToken ct,
        TrialCell? cell = null,
        int overloadRate = 0
    )
    {
        if (phase != "verify")
        {
            return await LoadTrial.RunAsync(
                plan,
                environment,
                bundle,
                Spec(plan, scenario, environment.Provider, phase, repetition, offeredRate, cell, overloadRate),
                ct
            );
        }
        var tag = $"{environment.Provider}-{scenario.Id}-{phase}-{repetition}-0";
        var trialStarted = Stopwatch.GetTimestamp();
        Console.WriteLine($"  START {tag}");
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var oracle = scenario.Requirements.Contains("external_oracle", StringComparer.Ordinal)
            ? new EffectService(bundle.Resolve($"repro/state/{tag}/oracle.db"))
            : null;
        if (oracle is not null)
        {
            await oracle.StartAsync(ct);
        }
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        bundle.Write($"histories/{tag}-settings.json", await state.CaptureSettingsAsync(ct), VerifyJson.Default.DictionaryStringString);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var reader = new DurableReader(state);
        // L03 verification submits one batch of each size in the sweep.
        int[] batchSweep =
        [
            .. plan.FullPolicy.GetProperty("measurement").GetProperty("batch_size_sweep").EnumerateArray().Select(e => e.GetInt32()),
        ];
        var count =
            scenario.Id is "P02" or "P03" ? 1
            : scenario.Id == "L03" ? batchSweep.Sum()
            : 12;
        var input = Enumerable.Range(0, count).Select(i => WorkloadModel.Input(scenario.Id, LoadTrial.Key(i), i, plan.Seed)).ToArray();
        var expected = WorkloadModel.Expected(input);
        var expectedRoots = expected.Where(e => e.Parent is null).ToDictionary(e => e.Key, StringComparer.Ordinal);
        using var workers = new OwnedWorkerPool();
        // These cases admit their jobs before any worker runs: the signal must land before its wait registers, or
        // admission is what the case is about.
        var delayWorkers = scenario.Id is "L02" or "L03" or "L04" or "T02";
        if (!delayWorkers)
        {
            await workers.StartAsync(state, oracle, 1, null, ct, 1, scenario.Id == "P08");
        }
        var background = scenario.Id is "S02" or "S03"
            ? await StorageCases.PrepareBackgroundAsync(plan, scenario, state, jobs, null, ct)
            : 0;
        var floor = await reader.LastJobIdAsync(ct);
        Dictionary<string, long> acknowledged = new(StringComparer.Ordinal);
        HashSet<string> ambiguous = new(StringComparer.Ordinal);
        HashSet<string> failedSignals = new(StringComparer.Ordinal);
        List<Check> boundaryChecks = [];
        if (scenario.Id == "L03")
        {
            var next = 0;
            foreach (var size in batchSweep)
            {
                var batch = input.Skip(next).Take(size).ToArray();
                next += size;
                try
                {
                    var response = await jobs.EnqueueBatchAsync([.. batch.Select(w => Request(w, scenario.Id, 0))], ct);
                    for (var i = 0; i < batch.Length; i++)
                    {
                        acknowledged[batch[i].Key] = response.Jobs[i].JobId;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ambiguous.UnionWith(batch.Select(w => w.Key));
                }
            }
        }
        else
        {
            for (var i = 0; i < input.Length; i++)
            {
                try
                {
                    var accepted = await jobs.EnqueueAsync(Request(input[i], scenario.Id, i), ct);
                    acknowledged[input[i].Key] = accepted.JobId;
                    if (input[i].Signal is not null && scenario.Id == "T02")
                    {
                        try
                        {
                            await jobs.RaiseSignalAsync(accepted, input[i].Signal!, default(JobPayload), ct: ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failedSignals.Add(input[i].Key);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ambiguous.Add(input[i].Key);
                }
            }
        }
        // An enqueue whose reply was lost may still have committed: its identity decides, by its deduplication key.
        foreach (var key in ambiguous.ToArray())
        {
            if (await jobs.GetAsync(JobLookup.ByDeduplicationKey("bench", key), ct) is { } committed)
            {
                acknowledged[key] = committed.JobId;
                ambiguous.Remove(key);
            }
        }
        if (delayWorkers)
        {
            await workers.StartAsync(state, oracle, 1, null, ct, 1, scenario.Id == "P08");
        }
        var children = expected.Any(e => e.Parent is not null);
        HashSet<long> signalled = [];
        HashSet<long> live = [];
        var drainStart = Stopwatch.GetTimestamp();
        var lowest = floor + 1;
        var drained = await plan.TryDrainAsync(
            async () =>
            {
                if (children)
                {
                    boundaryChecks.AddRange(await reader.LiveChildChecksAsync(floor, live, ct));
                }
                // T04 and T06 roots wait on a signal only the controller raises.
                if (scenario.Id is "T04" or "T06")
                {
                    foreach (var row in await reader.ReadAsync(null, ct, floor))
                    {
                        if (row.Status == 20 && signalled.Add(row.JobId))
                        {
                            await jobs.RaiseSignalAsync(JobLookup.ById(row.JobId), "go", default(JobPayload), ct: ct);
                            if (scenario.Id == "T06")
                            {
                                await jobs.RaiseSignalAsync(JobLookup.ById(row.JobId), "go", default(JobPayload), ct: ct);
                            }
                        }
                    }
                }
                (var left, lowest) = await reader.UnfinishedRootsAsync(lowest, ct);
                return left;
            },
            _ => plan.Deadline(expected.Length, 0, state.DrainRate),
            drainStart,
            plan.ObserverCadenceMs,
            ct
        );
        var waited = Stopwatch.GetTimestamp();
        var rows = await reader.ReadAsync(null, ct, floor);
        var steps = await reader.StepsAsync(null, ct, floor);
        var observed = DurableReader.Outcomes(rows, steps);
        var byId = rows.ToDictionary(r => r.JobId);
        foreach (var (key, id) in acknowledged)
        {
            if (!byId.TryGetValue(id, out var row))
            {
                continue;
            }
            var wanted = expectedRoots[key];
            if (row.Key != key || row.ParentJobId is not null || row.JobNamespace != wanted.JobNamespace)
            {
                boundaryChecks.Add(
                    new(
                        "acknowledged-identity",
                        key,
                        false,
                        "The acknowledged database ID did not identify the expected root and namespace."
                    )
                );
            }
            if (row.Status is 100 or 200 or 220 && (DurableReader.Status(row.Status) != wanted.Status || row.Output != wanted.Output))
            {
                boundaryChecks.Add(
                    new("observed-terminal-outcome", key, false, "The terminal row did not contain the expected logical result and status.")
                );
            }
        }
        var submitted = acknowledged.Keys.Concat(ambiguous).ToHashSet(StringComparer.Ordinal);
        Check[] checks =
        [
            .. OutcomeOracle.Reconcile(expected.Where(e => submitted.Contains(e.Key.Split('/')[0])), observed),
            .. boundaryChecks,
            .. background > 0 ? await StorageCases.CheckBackgroundAsync(state, scenario.Id, background, ct) : [],
            .. oracle is null
                ? []
                : await oracle.CheckIdempotentKeysAsync(
                    input.Where(i => i.EffectMode == "idempotent" && acknowledged.ContainsKey(i.Key)).Select(i => i.Key),
                    ct
                ),
        ];
        bundle.Write($"histories/{tag}-checks.json", checks, VerifyJson.Default.CheckArray);
        var actualRoots = observed.Where(o => o.Parent is null).ToDictionary(o => o.Key, StringComparer.Ordinal);
        var unresolvedKeys = submitted
            .Where(k => !actualRoots.TryGetValue(k, out var o) || o.Status is not ("Succeeded" or "Failed" or "Cancelled"))
            .ToHashSet(StringComparer.Ordinal);
        // An active row, or an admission whose reply was lost and that left no row, establishes an unresolved outcome, not
        // a wrong final result; an acknowledged identity absent from durable storage remains a safety failure.
        var failures = OutcomeOracle.SafetyFailures(checks, unresolvedKeys, acknowledged.ContainsKey);
        // A root whose signal call failed may wait forever without any engine fault: its liveness is unknown.
        var livenessFailed = unresolvedKeys.Any(k => acknowledged.ContainsKey(k) && !failedSignals.Contains(k));
        var exercised = observed.Length > 0 && submitted.Count > 0;
        var status =
            failures.Length > 0 || livenessFailed ? "FAIL"
            : unresolvedKeys.Count > 0 || !state.SettingsVerified || !exercised || failedSignals.Count > 0 ? "INCONCLUSIVE"
            : "PASS";
        var failing = checks
            .Where(c => !c.Passed)
            .Select(c => c.Identity.Split('/')[0])
            .Concat(unresolvedKeys)
            .Concat(failedSignals)
            .ToHashSet(StringComparer.Ordinal);
        string[] rowEvidence =
            status == "PASS"
                ? []
                : bundle.WriteRows(
                    tag,
                    false,
                    expected.Where(e => failing.Contains(e.Key.Split('/')[0])),
                    observed.Where(o => failing.Contains(o.Key.Split('/')[0]))
                );
        Dictionary<string, Metric> metrics = new(StringComparer.Ordinal)
        {
            ["setup_seconds"] = new(Seconds(drainStart - trialStarted), "seconds", Reason: "Schema, host, workers and admission."),
            ["window_seconds"] = new(Seconds(waited - drainStart), "seconds", Reason: "The wait for every root to finish."),
            ["verify_seconds"] = new(Seconds(Stopwatch.GetTimestamp() - waited), "seconds", Reason: "The read and the checks."),
            ["acknowledged_roots"] = new(acknowledged.Count, "roots"),
            ["unresolved_roots"] = new(unresolvedKeys.Count, "roots"),
            ["failed_signals"] = new(failedSignals.Count, "roots"),
            ["drained"] = new(drained ? 1 : 0, "boolean"),
        };
        return new(
            scenario.Id,
            environment.Provider,
            phase,
            repetition,
            status,
            failures.Length > 0 ? $"{failures.Length} failed identity/output checks; exact identities retained."
                : failedSignals.Count > 0
                    ? $"{failedSignals.Count} signal calls failed after their admissions were acknowledged; those admissions stay in the safety checks."
                : unresolvedKeys.Count > 0 ? "Accepted cohort remains unresolved."
                : !exercised ? "No submitted logical workload was observed in durable state; the required workload was not exercised."
                : null,
            failures.Length > 0 ? "FAIL"
                : unresolvedKeys.Count > 0 || !exercised ? "UNVERIFIED"
                : "PASS",
            livenessFailed ? "FAIL"
                : unresolvedKeys.Count > 0 || !exercised ? "UNVERIFIED"
                : "PASS",
            exercised && failedSignals.Count == 0 ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            metrics,
            [$"histories/{tag}-checks.json", .. rowEvidence],
            "Acta native jobs/checkpoints/children; independent committed-state SQL observer"
        );
    }

    /// <summary>The measured trial a case's phase runs, at <paramref name="offeredRate"/>, in today's case vocabulary.</summary>
    private static LoadSpec Spec(
        RunPlan plan,
        ScenarioDescriptor scenario,
        string provider,
        string phase,
        int repetition,
        int offeredRate,
        TrialCell? cell,
        int overloadRate
    )
    {
        var id = scenario.Id;
        var seconds =
            id == "E03" ? (int)plan.ProfilePolicy("overload_seconds")
            : phase == "discover" ? (int)plan.ProfilePolicy("discovery_seconds")
            : phase == "soak" ? plan.SoakSeconds
            : plan.MeasurementSeconds;
        // A measured window, and E03's overload, open after the warmup of the trial's own stream at the offered rate,
        // on warm processes already carrying the backlog that rate sustains.
        var warmup = phase == "measure" && id != "L04" || id == "E03" ? plan.WarmupSeconds : 0;
        var recoveryRate = Math.Max(
            1,
            (int)(offeredRate * plan.Policy("capacity", "overload", "recovery_rate_fraction_of_validated_capacity"))
        );
        LoadSegment[] segments =
            id == "E03"
                ? [new(warmup, offeredRate), new(seconds / 2, overloadRate), new(seconds - seconds / 2, recoveryRate)]
                : [new(warmup + seconds, offeredRate)];
        var payload = cell?.PayloadBytes;
        var spec = new LoadSpec(
            id,
            phase,
            repetition,
            offeredRate,
            id is "L02" or "L03" ? LoadMode.Admission : LoadMode.Stream,
            segments,
            0,
            warmup,
            seconds,
            plan.ProducerCalls[provider],
            id == "L03" ? cell?.BatchSize ?? plan.AdmissionBatchSize : 1,
            cell?.Workers ?? 1,
            cell?.Executors,
            cell?.Namespaces ?? 1,
            plan.LatencySloMs.GetValueOrDefault(id, plan.ReferenceSloMs),
            i => WorkloadModel.Input(id, LoadTrial.Key(i), i, plan.Seed, payload),
            i => Lane(id, i),
            ConcurrencyKey(id),
            scenario.Requirements.Contains("external_oracle", StringComparer.Ordinal),
            id == "P08",
            id == "E02",
            cell?.Name,
            payload,
            id == "E03" ? overloadRate : 0,
            id is "S02" or "S03"
                ? (jobs, state, resources, ct) => StorageCases.PrepareBackgroundAsync(plan, scenario, state, jobs, resources, ct)
                : null,
            id is "S02" or "S03" ? (state, population, ct) => StorageCases.CheckBackgroundAsync(state, id, population, ct) : null
        );
        return id == "L04"
            ? spec with
            {
                Mode = LoadMode.Cohort,
                Segments = [],
                Cohort = plan.PreloadedBacklogRoots[provider],
                WindowSeconds = 0,
                Calls = (int)plan.Policy("measurement", "preload_producers"),
                JobsPerCall = (int)plan.Policy("measurement", "preloaded_backlog_admission_batch_size"),
            }
            : spec;
    }

    private static readonly string[] Lanes = [.. Enumerable.Range(0, 1000).Select(i => $"lane-{i}")];

    private static string? Lane(string id, int index) =>
        id == "O01" ? Lanes[index % Lanes.Length]
        : id == "O02" ? "hot"
        : null;

    private static string? ConcurrencyKey(string id) => id == "O03" ? "hot" : null;

    private static EnqueueJobRequest Request(BenchWork input, string scenario, int index) =>
        new(
            "bench",
            "work",
            JobPayload.Json(input, WorkJson.Default.BenchWork),
            DeduplicationKey: input.Key,
            Lane: Lane(scenario, index),
            ConcurrencyKey: ConcurrencyKey(scenario)
        );

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
