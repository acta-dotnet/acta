using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

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
        var tag = $"{environment.Provider}-{scenario.Id}-{phase}-{repetition}-{offeredRate}" + (cell is null ? "" : "-" + cell.Name);
        var executors = cell?.Executors;
        var workerCount = cell?.Workers ?? 1;
        var namespaceCount = cell?.Namespaces ?? 1;
        var admissionBatchSize =
            scenario.Id == "L04"
                ? (int)plan.Policy("measurement", "preloaded_backlog_admission_batch_size")
                : cell?.BatchSize ?? plan.AdmissionBatchSize;
        // L03 verification submits one batch of each size in the sweep.
        int[] batchSweep =
        [
            .. plan.FullPolicy.GetProperty("measurement").GetProperty("batch_size_sweep").EnumerateArray().Select(e => e.GetInt32()),
        ];
        string NamespaceAt(int index) => index % namespaceCount == 0 ? "bench" : "bench-2";
        var trialStarted = Stopwatch.GetTimestamp();
        var writeSecondsAtStart = bundle.WriteSeconds;
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
        var settings = await state.CaptureSettingsAsync(ct);
        bundle.Write($"histories/{tag}-settings.json", settings, BenchJson.Default.DictionaryStringString);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var reader = new DurableReader(state);
        var seconds =
            phase == "verify" ? 0
            : scenario.Id == "E03" ? (int)plan.ProfilePolicy("overload_seconds")
            : phase == "discover" ? (int)plan.ProfilePolicy("discovery_seconds")
            : phase == "soak" ? plan.SoakSeconds
            : plan.MeasurementSeconds;
        // A measured trial's window, and E03's overload, open after the first WarmupSeconds of the trial's own
        // open-loop stream at the offered rate, on warm processes already carrying the backlog that rate sustains;
        // from an empty start the backlog climbs at any rate. The fixed-cohort drain has no stream to warm.
        var warmupSeconds = phase == "measure" && scenario.Id != "L04" || scenario.Id == "E03" ? plan.WarmupSeconds : 0;
        var recoveryRate = Math.Max(
            1,
            (int)(offeredRate * plan.Policy("capacity", "overload", "recovery_rate_fraction_of_validated_capacity"))
        );
        var warmupCount = warmupSeconds * offeredRate;
        var overloadCount = seconds / 2 * overloadRate;
        var count =
            scenario.Id == "E03" ? warmupCount + overloadCount + (seconds - seconds / 2) * recoveryRate
            : seconds == 0
                ? scenario.Id is "P02" or "P03" ? 1
                    : scenario.Id == "L03" ? batchSweep.Sum()
                    : 12
            : scenario.Id == "L04" ? plan.PreloadedBacklogRoots[environment.Provider]
            : checked((warmupSeconds + seconds) * offeredRate);
        if (!CohortFitsBudget(scenario.Id, count, cell))
        {
            throw new ResourceBudgetException("Frozen cohort model exceeds the controller memory budget; cohort was not silently reduced.");
        }
        var input = Enumerable
            .Range(0, count)
            .Select(i =>
            {
                var work = WorkloadModel.Input(scenario.Id, $"root-{i}", i, plan.Seed);
                return cell?.PayloadBytes is { } length
                    ? work with
                    {
                        Payload = WorkloadModel.Payload(length, unchecked(plan.Seed + i)),
                    }
                    : work;
            })
            .ToArray();
        var expected = WorkloadModel.Expected(
            input,
            input
                .Select((work, i) => (work.Key, Namespace: NamespaceAt(i)))
                .ToDictionary(p => p.Key, p => p.Namespace, StringComparer.Ordinal)
        );
        var expectedRoots = expected.Where(e => e.Parent is null).ToDictionary(e => e.Key, StringComparer.Ordinal);
        using var workers = new OwnedWorkerPool();
        var delayWorkers = scenario.Id is "L02" or "L03" or "L04" or "T02";
        if (!delayWorkers)
        {
            await workers.StartAsync(state, oracle, workerCount, executors, ct, namespaceCount, scenario.Id == "P08");
        }
        await using var resources = new ResourceSampler(state, bundle, tag, workers.ProcessIds, ct);
        if (oracle is not null)
        {
            resources.AddProcess(oracle.ProcessId, "external_oracle");
        }
        var seeding = Stopwatch.GetTimestamp();
        var backgroundPopulation = scenario.Id is "S02" or "S03"
            ? await StorageCases.PrepareBackgroundAsync(plan, scenario, state, jobs, resources, ct)
            : 0;
        var backgroundTicks = Stopwatch.GetTimestamp() - seeding;
        // Completions are timed by the database's own stamps, and the trial's jobs are those above the floor.
        var clock = await reader.ClockAsync(ct);
        var floor = await reader.LastJobIdAsync(ct);
        var streamStart = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        var start = streamStart + warmupSeconds * Stopwatch.Frequency;
        var end = seconds == 0 ? long.MaxValue : start + seconds * Stopwatch.Frequency;
        var admissions = new ConcurrentDictionary<string, Admission>(StringComparer.Ordinal);
        Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
        List<Check> boundaryChecks = [];
        HashSet<long> signalled = [];
        var failedSignals = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        List<DrainSample> drainSamples = [];
        var drainPath = scenario.Id == "L04";
        long acknowledged = 0,
            firstId = 0,
            lastId = 0,
            landed = 0,
            drainEnded = 0;
        var arrivals = Channel.CreateBounded<int>(
            new BoundedChannelOptions(plan.BufferCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait }
        );
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerToken = producerCancellation.Token;
        var producing = true;
        var workerStartupMs = 0.0;
        var queryLatency = new LatencyHistogram();
        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var observer = ObserveAsync();
        // Idle producers wait on the channel, so submissions in flight follow offered rate x enqueue latency up to
        // the protocol's limit. A small fixed pool would cap admission at pool size / enqueue latency. The L04 preload
        // is outside the measurement, so its batches go in concurrently.
        var producers =
            scenario.Id == "L03" ? new[] { SubmitBatchesAsync() }
            : scenario.Id == "L04"
                ? Enumerable.Range(0, (int)plan.Policy("measurement", "preload_producers")).Select(_ => SubmitBatchesAsync()).ToArray()
            : Enumerable.Range(0, plan.ProducerConcurrency).Select(_ => SubmitAsync()).ToArray();
        var finished = false;
        var drain = Task.CompletedTask;
        List<DurableRow> rows = [];
        StepObservation[] steps = [];
        Check[] storedChecks = [];
        var submittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var arrivalsStarted = Stopwatch.GetTimestamp();
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (resources.BudgetExceeded)
                {
                    throw new ResourceBudgetException("Declared memory/disk resource budget exceeded; evidence retained.");
                }
                var offset =
                    scenario.Id == "L03" ? (i / admissionBatchSize * admissionBatchSize) / (double)Math.Max(1, offeredRate)
                    : scenario.Id == "E03" && i >= warmupCount
                        ? i < warmupCount + overloadCount ? warmupSeconds + (i - warmupCount) / (double)overloadRate
                            : warmupSeconds + seconds / 2.0 + (i - warmupCount - overloadCount) / (double)recoveryRate
                    : i / (double)Math.Max(1, offeredRate);
                var scheduled =
                    seconds == 0 || scenario.Id == "L04" ? Stopwatch.GetTimestamp() : streamStart + (long)(offset * Stopwatch.Frequency);
                admissions[input[i].Key] = new(input[i].Key, scheduled, null, null, null, "intended");
                // Task.Delay can wake before its interval elapses, so wait until the clock passes the scheduled time.
                while (seconds > 0 && scenario.Id != "L04" && Stopwatch.GetTimestamp() < scheduled)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Ms(scheduled - Stopwatch.GetTimestamp()))), ct);
                }
                // L02 measures admission itself: an arrival waits for a producer and the wait shows as admission lag.
                if (seconds == 0 || scenario.Id is "L02" or "L04")
                {
                    await arrivals.Writer.WriteAsync(i, ct);
                }
                else if (!arrivals.Writer.TryWrite(i))
                {
                    admissions[input[i].Key] = admissions[input[i].Key] with { Outcome = "missed" };
                }
            }
            arrivals.Writer.Complete();
            await Task.WhenAll(producers);
            // A missing acknowledgement is not a rejection. Recover committed identities
            // before closing the observer's cohort, while retaining the original ambiguity.
            if (admissions.Values.Any(a => a.Outcome == "ambiguous"))
            {
                for (var i = 0; i < input.Length; i++)
                {
                    var admission = admissions[input[i].Key];
                    if (admission.Outcome == "ambiguous")
                    {
                        var committed = await jobs.GetAsync(JobLookup.ByDeduplicationKey(NamespaceAt(i), input[i].Key), ct);
                        if (committed is not null)
                        {
                            RecordAdmission(admission with { JobId = committed.JobId });
                        }
                    }
                }
            }
            long[] ids = [.. admissions.Values.Where(a => a.JobId is not null).Select(a => a.JobId!.Value)];
            acknowledged = ids.Length;
            if (drainPath)
            {
                (firstId, lastId) = (ids.DefaultIfEmpty().Min(), ids.DefaultIfEmpty().Max());
                // The drain counts by job-id range, so the range must hold the cohort's roots and no other row. Concurrent
                // batches may leave unused ids in it, so the rows are counted rather than the ids.
                if (ids.Length > 0 && await reader.RangeCountAsync(firstId, lastId, ct) is var inRange && inRange != ids.Length)
                {
                    throw new InvalidOperationException(
                        $"The job-id range {firstId}..{lastId} of the {ids.Length} acknowledged roots holds {inRange} jobs; a count over it would include other rows."
                    );
                }
                start = Stopwatch.GetTimestamp();
                end = seconds == 0 ? long.MaxValue : start + seconds * Stopwatch.Frequency;
            }
            Volatile.Write(ref producing, false);
            drain = DrainAsync(observationCancellation.Token);
            if (delayWorkers)
            {
                var startup = Stopwatch.GetTimestamp();
                await workers.StartAsync(state, oracle, workerCount, executors, ct, namespaceCount, scenario.Id == "P08");
                workerStartupMs = Ms(Stopwatch.GetTimestamp() - startup);
                foreach (var pid in workers.ProcessIds)
                {
                    resources.AddProcess(pid, "worker");
                }
            }
            await drain;
            drainEnded = Stopwatch.GetTimestamp();
            await observationCancellation.CancelAsync();
            try
            {
                await observer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            // One paged read verifies every job and times every completion by its database stamp. The verification
            // reads stay inside the trial, so a trial whose verification fails still writes its rows.
            rows = await reader.ReadAllAsync(floor, ct);
            steps = await reader.StepsAsync(null, ct, floor);
            submittedKeys.UnionWith(admissions.Values.Where(a => a.Outcome != "missed").Select(a => a.Key));
            storedChecks =
            [
                .. backgroundPopulation > 0 ? await StorageCases.CheckBackgroundAsync(state, scenario.Id, backgroundPopulation, ct) : [],
                .. oracle is null
                    ? []
                    : await oracle.CheckIdempotentKeysAsync(
                        input.Where(i => i.EffectMode == "idempotent" && submittedKeys.Contains(i.Key)).Select(i => i.Key),
                        ct
                    ),
            ];
            finished = true;
        }
        finally
        {
            arrivals.Writer.TryComplete();
            await producerCancellation.CancelAsync();
            await observationCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(producers.Append(observer).Append(drain));
            }
            catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested) { }
            // A faulted observer or drain means the try block already threw; that exception stands and the histories are
            // still written.
            catch (Exception) when (observer.IsFaulted || drain.IsFaulted) { }
            if (!finished)
            {
                WriteRows(_ => true, []);
            }
            if (drainPath)
            {
                File.WriteAllLines(
                    bundle.Resolve($"histories/{tag}-progress.csv"),
                    drainSamples
                        .Select(s =>
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{Ms(s.Timestamp - start) / 1000:R},{s.Completed},{Ms(s.QueryTicks) / 1000:R}"
                            )
                        )
                        .Prepend("elapsed_seconds,completed,query_seconds")
                );
            }
            bundle.Write(
                $"histories/{tag}-window.json",
                new Dictionary<string, string>
                {
                    ["stream_start_ticks"] = streamStart.ToString(CultureInfo.InvariantCulture),
                    ["start_ticks"] = start.ToString(CultureInfo.InvariantCulture),
                    ["end_ticks"] = end.ToString(CultureInfo.InvariantCulture),
                    ["clock_frequency"] = Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture),
                    ["database_clock_ticks"] = clock.Ticks.ToString(CultureInfo.InvariantCulture),
                    ["database_clock_database_ticks"] = clock.DatabaseTicks.ToString(CultureInfo.InvariantCulture),
                    ["database_clock_round_trip_ticks"] = clock.RoundTripTicks.ToString(CultureInfo.InvariantCulture),
                    ["floor_job_id"] = floor.ToString(CultureInfo.InvariantCulture),
                    ["latency_origin"] =
                        "Each root's original scheduled admission to its database completion stamp; preload time is retained for L04.",
                    ["throughput_boundary"] =
                        scenario.Id == "L04"
                            ? "Cohort drain after preload, including worker startup."
                            : "Half-open fixed measurement window after the stream's warmup prefix; drain completions are excluded.",
                },
                BenchJson.Default.DictionaryStringString
            );
        }
        var observed = DurableReader.Outcomes(rows, steps);
        var byId = rows.ToDictionary(r => r.JobId);
        foreach (var duplicate in admissions.Values.Where(a => a.JobId is not null).GroupBy(a => a.JobId).SelectMany(g => g.Skip(1)))
        {
            boundaryChecks.Add(
                new("acknowledged-identity", duplicate.Key, false, "Two logical roots were acknowledged with the same database identity.")
            );
        }
        foreach (var admission in admissions.Values)
        {
            if (admission.JobId is not { } id || !byId.TryGetValue(id, out var row))
            {
                continue;
            }
            var wanted = expectedRoots[admission.Key];
            if (row.Key != admission.Key || row.ParentJobId is not null || row.JobNamespace != wanted.JobNamespace)
            {
                boundaryChecks.Add(
                    new(
                        "acknowledged-identity",
                        admission.Key,
                        false,
                        "The acknowledged database ID did not identify the expected root and namespace. Its observation cannot support a capacity claim."
                    )
                );
            }
            if (row.Status is 100 or 200 or 220)
            {
                observations[admission.Key] = new(admission.Key, id, clock.ToStopwatch(row.Modified), row.Status, row.Output);
                if (DurableReader.Status(row.Status) != wanted.Status || row.Output != wanted.Output)
                {
                    boundaryChecks.Add(
                        new(
                            "observed-terminal-outcome",
                            admission.Key,
                            false,
                            "The terminal row did not contain the expected logical result and status."
                        )
                    );
                }
            }
        }
        foreach (var descendant in rows.Where(r => r.ParentJobId is not null && r.Status is not (100 or 200 or 220)))
        {
            if (descendant.LineageRootJobId is { } rootId && byId.TryGetValue(rootId, out var root) && root.Status is 100 or 200 or 220)
            {
                boundaryChecks.Add(
                    new(
                        "terminal-parent-live-child",
                        descendant.Key,
                        false,
                        "A root reached a terminal outcome before its required child was terminal."
                    )
                );
            }
        }
        var drainedAt = drainPath && acknowledged > 0 ? DrainCurve.Reached(drainSamples, acknowledged) : null;
        long[] completions = [.. rows.Where(r => r.ParentJobId is null && r.Status is 100 or 200 or 220).Select(r => r.Modified).Order()];
        long[] completionClock =
            drainedAt is null || completions.Length != acknowledged
                ? []
                :
                [
                    completions[0],
                    completions[DrainCurve.PercentileRank(acknowledged, 20)],
                    completions[DrainCurve.PercentileRank(acknowledged, 80)],
                    completions[^1],
                ];
        Check[] checks =
        [
            .. OutcomeOracle.Reconcile(expected.Where(e => submittedKeys.Contains(e.Key.Split('/')[0])), observed),
            .. boundaryChecks,
            .. storedChecks,
        ];
        bundle.Write($"histories/{tag}-checks.json", checks, BenchJson.Default.CheckArray);
        var backlogSamples = BacklogTrend.Reconstruct(
            admissions
                .Values.Where(a => a.AcknowledgedTicks is not null)
                .Select(a => (a.AcknowledgedTicks!.Value, observations.TryGetValue(a.Key, out var o) ? o.CompletedTicks : long.MaxValue)),
            streamStart,
            drainEnded,
            (long)plan.Policy("measurement", "progress_count_ms") * Stopwatch.Frequency / 1000
        );
        File.WriteAllLines(
            bundle.Resolve($"histories/{tag}-backlog.csv"),
            backlogSamples
                .Select(s => string.Create(CultureInfo.InvariantCulture, $"{Ms(s.Timestamp - streamStart) / 1000:R},{s.Backlog}"))
                .Prepend("elapsed_seconds,backlog")
        );
        var scheduledLatency = new LatencyHistogram();
        var actualLatency = new LatencyHistogram();
        foreach (var o in observations.Values)
        {
            if (admissions.TryGetValue(o.Key, out var a) && Measured(a))
            {
                // A completion stamp mapped across the clocks can land a fraction of the pairing's round trip early.
                scheduledLatency.Record(Math.Max(0, Ms(o.CompletedTicks - a.ScheduledTicks)));
                if (a.SubmittedTicks is { } submitted)
                {
                    actualLatency.Record(Math.Max(0, Ms(o.CompletedTicks - submitted)));
                }
            }
        }
        bundle.Write($"histograms/{tag}-scheduled.json", scheduledLatency.Snapshot(), BenchJson.Default.HistogramSnapshot);
        bundle.Write($"histograms/{tag}-submitted.json", actualLatency.Snapshot(), BenchJson.Default.HistogramSnapshot);
        var actualRoots = observed.Where(o => o.Parent is null).ToDictionary(o => o.Key, StringComparer.Ordinal);
        var unresolvedRoots = admissions
            .Values.Where(a =>
                a.Outcome != "missed"
                && (!actualRoots.TryGetValue(a.Key, out var outcome) || outcome.Status is not ("Succeeded" or "Failed" or "Cancelled"))
            )
            .ToArray();
        var unresolvedKeys = unresolvedRoots.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        // An active row establishes an unresolved outcome, not a wrong final result.
        // An acknowledged identity absent from durable storage remains a safety failure.
        var failures = OutcomeOracle.SafetyFailures(
            checks,
            unresolvedKeys,
            identity => admissions.TryGetValue(identity, out var admission) && admission.Outcome == "acknowledged"
        );
        var unresolved = unresolvedRoots.Length;
        // A root whose signal call failed may wait forever without any engine fault: its liveness is unknown.
        var livenessFailed = unresolvedRoots.Any(a => a.Outcome == "acknowledged" && !failedSignals.ContainsKey(a.Key));
        var admissionLatency = new LatencyHistogram();
        var generatorLatency = new LatencyHistogram();
        var measuredAdmissions = admissions.Values.Where(Measured).ToArray();
        foreach (var admission in measuredAdmissions)
        {
            if (admission.AcknowledgedTicks is { } ack)
            {
                admissionLatency.Record(Ms(ack - admission.ScheduledTicks));
            }
            if (admission.SubmittedTicks is { } submitted)
            {
                generatorLatency.Record(Ms(submitted - admission.ScheduledTicks));
            }
        }
        bundle.Write($"histograms/{tag}-admission.json", admissionLatency.Snapshot(), BenchJson.Default.HistogramSnapshot);
        bundle.Write($"histograms/{tag}-generator-lag.json", generatorLatency.Snapshot(), BenchJson.Default.HistogramSnapshot);
        var generatorLate = measuredAdmissions.Count(a =>
            a.SubmittedTicks is { } submitted && Ms(submitted - a.ScheduledTicks) > plan.GeneratorLagToleranceMs
        );
        var missed = measuredAdmissions.Count(a => a.Outcome == "missed");
        var validRootKeys = expected
            .Where(e =>
                e.Parent is null
                && e.Status == "Succeeded"
                && actualRoots.TryGetValue(e.Key, out var root)
                && root.Status == e.Status
                && root.Output == e.Output
            )
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);
        var windowCompleted = observations.Values.Count(o =>
            validRootKeys.Contains(o.Key) && Verdicts.InWindow(o.CompletedTicks, start, end)
        );
        var p99 = scheduledLatency.Percentile(0.99);
        var completedInWindow = observations.Values.Where(o => o.CompletedTicks < end).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var backlog = admissions.Values.Count(a => a.AcknowledgedTicks < end && !completedInWindow.Contains(a.Key));
        var measuredBacklog = backlogSamples.Where(t => t.Timestamp >= start && t.Timestamp < end).ToArray();
        var backlogSlope = BacklogTrend.Slope(measuredBacklog, Stopwatch.Frequency);
        var admissionPath = scenario.Id is "L02" or "L03";
        var censored = admissions.Values.Count(a => a.Outcome != "missed" && !observations.ContainsKey(a.Key));
        var observerLimited =
            !admissionPath && censored > 0 || scheduledLatency.Snapshot().Overflow > 0 || admissionLatency.Snapshot().Overflow > 0;
        var liveWorkers = workers.LiveCount;
        var workerExit = liveWorkers != workerCount;
        var measurement =
            seconds == 0
                ? "NOT_MEASURED"
                : Verdicts.Measurement(
                    measuredAdmissions.Length,
                    missed + (drainPath || scenario.Id == "L02" ? 0 : generatorLate),
                    observerLimited,
                    admissionPath ? admissionLatency.Percentile(0.99) ?? double.PositiveInfinity
                        : drainPath ? 0
                        : p99 ?? double.PositiveInfinity,
                    plan.LatencySloMs.GetValueOrDefault(scenario.Id, plan.ReferenceSloMs),
                    admissionPath || drainPath ? 0 : backlogSlope ?? double.PositiveInfinity,
                    SlopeTolerance(offeredRate, seconds),
                    plan.Policy("capacity", "max_generator_missed_fraction_for_valid_capacity")
                );
        if (seconds > 0 && (workerExit || !admissionPath && !drainPath && backlogSlope is null))
        {
            measurement = "UNAVAILABLE";
        }
        // E03 must show overload as backlog growth in its overload half, then a drain below the threshold in its
        // recovery half; a backlog sample above a fixed size alone is reached at any load by polling bursts.
        var overloadEnd = start + seconds / 2 * Stopwatch.Frequency;
        var overloadSlope = BacklogTrend.Slope(
            backlogSamples.Where(t => t.Timestamp >= start && t.Timestamp < overloadEnd).ToArray(),
            Stopwatch.Frequency
        );
        var recoveryThreshold = Math.Max(
            plan.Policy("capacity", "overload", "backlog_threshold_floor_roots"),
            overloadRate * plan.Policy("capacity", "overload", "backlog_threshold_fraction_of_overload_rate")
        );
        // Saturation has its own threshold, not the measured trials' stability band: that band is wide enough to absorb a
        // short window's jitter, and overload growth below it is still growth.
        var saturated =
            scenario.Id != "E03"
            || overloadSlope
                > Math.Max(
                    overloadRate * plan.Policy("capacity", "overload", "saturation_backlog_slope_fraction_of_overload_rate"),
                    seconds / 2 > 0 ? plan.Policy("capacity", "backlog_slope_resolution_floor_roots_per_interval") / (seconds / 2) : 0
                );
        var recovered =
            scenario.Id != "E03"
            || backlogSamples.Any(t => t.Timestamp >= overloadEnd && t.Timestamp < end && t.Backlog < recoveryThreshold);
        var overloadGap =
            !saturated ? $"The offered overload of {overloadRate} roots/s did not grow the backlog during the overload half."
            : !recovered ? $"The backlog did not fall below {recoveryThreshold} roots during the recovery half."
            : null;
        var overloadGates = overloadGap is not null && plan.MeasurementGatesVerdict;
        var metrics = Metrics();
        var exercised = observed.Length > 0 && submittedKeys.Count > 0;
        foreach (var (name, metric) in resources.Summary(validRootKeys.Count))
        {
            metrics[name] = metric;
        }
        // The trial's own phases; the ledger checks that follow it add ledger_seconds.
        var windowStart = drainPath ? start : streamStart;
        metrics["setup_seconds"] = new(
            Ms(streamStart - trialStarted - backgroundTicks) / 1000,
            "seconds",
            Reason: "Schema, host, workers and clock pairing."
        );
        metrics["seed_seconds"] = new(
            Ms(backgroundTicks + (drainPath ? start - arrivalsStarted : 0)) / 1000,
            "seconds",
            Reason: "Background population and the L04 preload."
        );
        metrics["window_seconds"] = new(Ms(drainEnded - windowStart) / 1000, "seconds", Reason: "The arrival stream and its drain.");
        metrics["verify_seconds"] = new(
            Ms(Stopwatch.GetTimestamp() - drainEnded) / 1000,
            "seconds",
            Reason: "The paged read, the identity and outcome checks, and the metrics."
        );
        metrics["evidence_seconds"] = new(bundle.WriteSeconds - writeSecondsAtStart, "seconds", Reason: "Evidence files the trial wrote.");
        var status =
            failures.Length > 0 || livenessFailed ? "FAIL"
            : unresolved > 0 || !state.SettingsVerified || overloadGates || workerExit || !exercised || !failedSignals.IsEmpty
                ? "INCONCLUSIVE"
            : "PASS";
        var failing = checks
            .Where(c => !c.Passed)
            .Select(c => c.Identity.Split('/')[0])
            .Concat(unresolvedKeys)
            .Concat(failedSignals.Keys)
            .ToHashSet(StringComparer.Ordinal);
        string[] rowEvidence = status == "PASS" ? [] : WriteRows(failing.Contains, observed);
        return new(
            scenario.Id,
            environment.Provider,
            phase,
            repetition,
            status,
            failures.Length > 0 ? $"{failures.Length} failed identity/output checks; exact identities retained."
                : !failedSignals.IsEmpty
                    ? $"{failedSignals.Count} signal calls failed after their admissions were acknowledged; those admissions stay in the safety checks."
                : unresolved > 0 ? "Accepted cohort remains unresolved."
                : overloadGates ? overloadGap
                : workerExit ? "An owned worker exited unexpectedly; this trial cannot establish clean performance."
                : !exercised ? "No submitted logical workload was observed in durable state; the required workload was not exercised."
                : overloadGap is null ? null
                : Verdicts.Informational(plan, overloadGap),
            failures.Length > 0 ? "FAIL"
                : unresolved > 0 || !exercised ? "UNVERIFIED"
                : "PASS",
            livenessFailed ? "FAIL"
                : unresolved > 0 || !exercised ? "UNVERIFIED"
                : "PASS",
            !overloadGates && !workerExit && exercised && failedSignals.IsEmpty ? "COMPLETE" : "INCOMPLETE",
            measurement,
            metrics,
            [
                $"histories/{tag}-checks.json",
                $"histories/{tag}-backlog.csv",
                $"histories/{tag}-window.json",
                .. drainPath ? [$"histories/{tag}-progress.csv"] : Array.Empty<string>(),
                .. rowEvidence,
            ],
            "Acta native jobs/checkpoints/children; independent committed-state SQL observer",
            DiagnosticCellName: cell?.Name
        );

        bool Measured(Admission admission) => warmupSeconds == 0 || admission.ScheduledTicks >= start;

        // A trial that does not pass keeps the controller's rows for the identities it could not verify, or for every
        // identity when it stopped before verification; the durable rows stay in its retained database.
        string[] WriteRows(Func<string, bool> keep, ObservedOutcome[] outcomes)
        {
            bundle.Write(
                $"histories/{tag}-admissions.json",
                admissions.Values.Where(a => keep(a.Key)).OrderBy(a => a.ScheduledTicks).ToArray(),
                TrialJson.Default.AdmissionArray
            );
            bundle.Write(
                $"histories/{tag}-observations.json",
                observations.Values.Where(o => keep(o.Key)).ToArray(),
                TrialJson.Default.ObservationArray
            );
            return
            [
                $"histories/{tag}-admissions.json",
                $"histories/{tag}-observations.json",
                .. bundle.WriteRows(
                    tag,
                    false,
                    expected.Where(e => keep(e.Key.Split('/')[0])),
                    outcomes.Where(o => keep(o.Key.Split('/')[0]))
                ),
            ];
        }

        void RecordAdmission(Admission value) => admissions[value.Key] = value;

        async Task SubmitAsync()
        {
            await foreach (var i in arrivals.Reader.ReadAllAsync(producerToken))
            {
                var item = input[i];
                var before = admissions[item.Key] with { SubmittedTicks = Stopwatch.GetTimestamp() };
                admissions[item.Key] = before;
                EnqueueJobResponse accepted;
                try
                {
                    accepted = await jobs.EnqueueAsync(Request(item, scenario.Id, i, NamespaceAt(i)), producerToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordAdmission(before with { Outcome = "ambiguous" });
                    continue;
                }
                RecordAdmission(
                    before with
                    {
                        AcknowledgedTicks = Stopwatch.GetTimestamp(),
                        JobId = accepted.JobId,
                        Outcome = "acknowledged",
                    }
                );
                if (item.Signal is not null && scenario.Id == "T02")
                {
                    try
                    {
                        await jobs.RaiseSignalAsync(accepted, item.Signal, default(JobPayload), ct: producerToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failedSignals.TryAdd(item.Key, 0);
                    }
                }
            }
        }

        async Task SubmitBatchesAsync()
        {
            var batchNumber = 0;
            while (await arrivals.Reader.WaitToReadAsync(producerToken))
            {
                var size =
                    scenario.Id == "L04" ? admissionBatchSize
                    : phase == "verify" ? batchSweep[batchNumber++ % batchSweep.Length]
                    : admissionBatchSize;
                List<int> batch = [];
                while (batch.Count < size && await arrivals.Reader.WaitToReadAsync(producerToken))
                {
                    while (batch.Count < size && arrivals.Reader.TryRead(out var index))
                    {
                        batch.Add(index);
                    }
                }
                // A concurrent producer can take the last arrivals between the wait and the read.
                if (batch.Count == 0)
                {
                    continue;
                }
                var submitted = Stopwatch.GetTimestamp();
                foreach (var i in batch)
                {
                    admissions[input[i].Key] = admissions[input[i].Key] with { SubmittedTicks = submitted, BatchSize = batch.Count };
                }
                try
                {
                    var response = await jobs.EnqueueBatchAsync(
                        batch.Select(i => Request(input[i], scenario.Id, i, NamespaceAt(i))).ToArray(),
                        producerToken
                    );
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var key = input[batch[i]].Key;
                        RecordAdmission(
                            admissions[key] with
                            {
                                JobId = response.Jobs[i].JobId,
                                AcknowledgedTicks = Stopwatch.GetTimestamp(),
                                Outcome = "acknowledged",
                            }
                        );
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    foreach (var i in batch)
                    {
                        RecordAdmission(admissions[input[i].Key] with { Outcome = "ambiguous" });
                    }
                }
            }
        }

        async Task ObserveAsync()
        {
            var token = observationCancellation.Token;
            var nextMetric = Stopwatch.GetTimestamp();
            var nextProgress = nextMetric + 5 * Stopwatch.Frequency;
            var nextLive = nextMetric;
            var children = expected.Any(e => e.Parent is not null);
            HashSet<long> live = [];
            while (!token.IsCancellationRequested)
            {
                var cycle = Stopwatch.GetTimestamp();
                // A root waits for its descendants, so a terminal root with one still ready, timed or executing is a
                // violation the verification read misses once that descendant finishes too.
                if (children && cycle >= nextLive)
                {
                    foreach (var (root, rootKey, descendant) in await reader.LiveDescendantsAsync(floor, token))
                    {
                        if (live.Add(descendant))
                        {
                            boundaryChecks.Add(
                                new(
                                    "terminal-parent-live-child",
                                    rootKey,
                                    false,
                                    $"Root job {root} reached a terminal outcome while its descendant job {descendant} was not terminal."
                                )
                            );
                        }
                    }
                    nextLive = cycle + (long)plan.Policy("measurement", "progress_count_ms") * Stopwatch.Frequency / 1000;
                }
                // T04 and T06 roots wait on a signal only the controller raises: the one per-job read during a trial.
                if (scenario.Id is "T04" or "T06")
                {
                    foreach (var row in await reader.ReadAsync(null, token, floor))
                    {
                        if (row.Status == 20 && signalled.Add(row.JobId))
                        {
                            await jobs.RaiseSignalAsync(JobLookup.ById(row.JobId), "go", default(JobPayload), ct: token);
                            if (scenario.Id == "T06")
                            {
                                await jobs.RaiseSignalAsync(JobLookup.ById(row.JobId), "go", default(JobPayload), ct: token);
                            }
                        }
                    }
                }
                if (scenario.Id == "E02" && cycle >= nextMetric)
                {
                    var operations = host.Services.GetRequiredService<IActaOperations>();
                    await operations.Ledger.ListJobsAsync(new(JobNamespace: "bench", PageSize: 20), token);
                    await operations.Ledger.ListEventsAsync(new(JobNamespace: "bench", PageSize: 20), token);
                    queryLatency.Record(Ms(Stopwatch.GetTimestamp() - cycle));
                    nextMetric += Stopwatch.Frequency;
                }
                if (cycle >= nextProgress)
                {
                    var elapsed = Math.Max(0, (cycle - start) / (double)Stopwatch.Frequency);
                    var accepted = admissions.Values.Count(a => a.Outcome == "acknowledged");
                    var message =
                        $"  {environment.Provider} {scenario.Id} {phase} trial {repetition} | {elapsed:F0}s | scheduled {admissions.Count}/{count} | acknowledged {accepted}"
                        + (Volatile.Read(ref producing) ? "" : $" | terminal {Volatile.Read(ref landed)}");
                    if (Console.IsOutputRedirected)
                    {
                        Console.WriteLine(message);
                    }
                    else
                    {
                        Console.Write("\r" + message.PadRight(Math.Max(0, Console.WindowWidth - 1)) + "\n");
                    }
                    nextProgress = cycle + 5 * Stopwatch.Frequency;
                }
                await Task.Delay(plan.ObserverCadenceMs, token);
            }
        }

        // The drain counts what remains with one aggregate query a sample: an L04 cohort through the claim and in-flight
        // indexes over its id range, any other trial over its roots above the floor, from the lowest one a count left
        // unfinished. An L04 root is never blocked, paused, or waiting without a timer, so those two indexes hold every
        // unfinished one.
        async Task DrainAsync(CancellationToken token)
        {
            var interval = (int)plan.Policy("measurement", "progress_count_ms");
            var drainStart = Stopwatch.GetTimestamp();
            var lastCompletion = drainStart;
            var lowest = floor + 1;
            int? deadline = null;
            while (true)
            {
                var queried = Stopwatch.GetTimestamp();
                long left;
                if (drainPath)
                {
                    left = await reader.RemainingCountAsync(firstId, lastId, token);
                }
                else
                {
                    (left, lowest) = await reader.UnfinishedRootsAsync(lowest, token);
                }
                var counted = Stopwatch.GetTimestamp();
                if (drainSamples.Count > 0 && acknowledged - left > drainSamples[^1].Completed)
                {
                    lastCompletion = counted;
                }
                drainSamples.Add(new(counted, acknowledged - left, counted - queried));
                Volatile.Write(ref landed, acknowledged - left);
                // A cohort serialized on one key can drain slower than the deadline's progress rate, so a drain still
                // landing completions keeps waiting past the deadline.
                deadline ??= plan.Deadline(left * expected.Length / Math.Max(1, count), 0, state.DrainRate);
                if (left == 0 || plan.DrainOverdue(Ms(counted - drainStart) / 1000, Ms(counted - lastCompletion) / 1000, deadline.Value))
                {
                    return;
                }
                await Task.Delay(interval, token);
            }
        }

        Dictionary<string, Metric> Metrics()
        {
            var snapshot = scheduledLatency.Snapshot();
            var minimumSamples = plan.Policy("measurement", "publication_p99_sample_policy", "minimum_observed_completed_samples");
            var primarySamples = admissionPath ? admissionLatency.Snapshot().Count : snapshot.Count;
            var result = new Dictionary<string, Metric>(StringComparer.Ordinal)
            {
                ["executors_per_worker"] = Worker(s => s.Executors, "executors"),
                ["worker_heartbeat"] = Worker(s => s.HeartbeatSeconds, "seconds"),
                ["worker_lease"] = Worker(s => s.LeaseSeconds, "seconds"),
                ["worker_safety_poll"] = Worker(s => s.SafetyPollMs, "milliseconds"),
                ["worker_startup"] = new(workerStartupMs, "milliseconds"),
                ["batch_size"] = new(
                    scenario.Id is "L03" or "L04" ? admissionBatchSize : 1,
                    "roots/call",
                    Reason: scenario.Id == "L03"
                        ? "Open batch arrivals; all jobs in a batch share their scheduled arrival. Last batch may be partial."
                        : null
                ),
                ["worker_processes"] = new(workerCount, "processes"),
                ["live_worker_processes_at_reconciliation"] = new(liveWorkers, "processes"),
                ["namespace_count"] = new(namespaceCount, "namespaces"),
                ["diagnostic_cell"] = new(cell is null ? 0 : 1, "boolean"),
                ["calibration_candidate"] = new(
                    phase == "discover"
                    && measurement is "VALID" or "UNSTABLE"
                    && (admissionPath ? admissionLatency.Percentile(0.99) : p99)
                        <= plan.LatencySloMs.GetValueOrDefault(scenario.Id, plan.ReferenceSloMs)
                        ? 1
                        : 0,
                    "boolean",
                    Reason: "Discovery calibrates using latency and generator/observer conditions. Short-window backlog fluctuations cannot establish capacity; fresh full-duration repetitions must separately qualify stability."
                ),
                ["payload_bytes"] = cell?.PayloadBytes is { } payloadBytes
                    ? new(payloadBytes, "bytes")
                    : Metric.Missing("bytes", "Declared workload uses its canonical input or size mixture."),
                ["background_jobs"] = new(backgroundPopulation, "jobs"),
                ["warmup_seconds"] = new(
                    warmupSeconds,
                    "seconds",
                    Reason: "Leading part of the same arrival stream; its arrivals are reconciled but excluded from latency and generator metrics."
                ),
                ["scheduled_window_seconds"] = new(seconds, "seconds"),
                ["intended_roots"] = new(count, "roots"),
                ["submitted_requests"] = new(
                    admissions.Values.Count(a => a.SubmittedTicks is not null),
                    "roots",
                    Reason: "Logical roots submitted; native batch calls are separately declared by batch_size."
                ),
                ["handler_attempts"] = Metric.Missing(
                    "attempts",
                    "No independent handler-entry collector is installed. Durable execution numbers are not presented as handler-entry counts."
                ),
                ["missed_arrivals"] = new(missed, "roots"),
                ["generator_late_arrivals"] = new(generatorLate, "roots"),
                ["generator_lag_tolerance"] = new(plan.GeneratorLagToleranceMs, "milliseconds"),
                ["generator_lag_p99"] = generatorLatency.Percentile(0.99) is { } generatorP99
                    ? new(generatorP99, "milliseconds")
                    : Metric.Missing("milliseconds", "No submissions."),
                ["admission_p50"] = admissionLatency.Percentile(0.5) is { } admissionP50
                    ? new(admissionP50, "milliseconds")
                    : Metric.Missing("milliseconds", "No acknowledged admissions."),
                ["admission_p99"] = admissionLatency.Percentile(0.99) is { } admissionP99
                    ? new(admissionP99, "milliseconds")
                    : Metric.Missing("milliseconds", "No acknowledged admissions."),
                ["censored_latency_roots"] = new(censored, "roots"),
                ["acknowledged_roots"] = new(admissions.Values.Count(a => a.Outcome == "acknowledged"), "roots"),
                ["ambiguous_admissions"] = new(admissions.Values.Count(a => a.Outcome == "ambiguous"), "roots"),
                ["failed_signals"] = new(failedSignals.Count, "roots"),
                ["reconciled_ambiguous_admissions"] = new(
                    admissions.Values.Count(a => a.Outcome == "ambiguous" && actualRoots.ContainsKey(a.Key)),
                    "roots"
                ),
                ["unresolved_roots"] = new(unresolved, "roots"),
                ["latency_samples"] = new(snapshot.Count, "roots"),
                ["durably_succeeded_children"] = new(observed.Count(o => o.Parent is not null && o.Status == "Succeeded"), "children"),
                ["committed_checkpoints"] = new(
                    observed.Sum(o => (o.Steps ?? []).Count(s => !s.EndsWith("=uncommitted", StringComparison.Ordinal))),
                    "checkpoints"
                ),
                ["window_child_completion_rate"] =
                    seconds == 0
                        ? Metric.Missing("children/second", "Untimed verification.")
                        : new(
                            rows.Count(r =>
                                r.ParentJobId is not null && r.Status == 100 && Verdicts.InWindow(clock.ToStopwatch(r.Modified), start, end)
                            ) / (double)seconds,
                            "children/second",
                            Reason: "Committed descendants by their database completion stamp."
                        ),
                ["window_checkpoint_rate"] =
                    seconds == 0
                        ? Metric.Missing("checkpoints/second", "Untimed verification.")
                        : new(
                            steps.Count(s => s.Status == 100 && Verdicts.InWindow(clock.ToStopwatch(s.ModifiedTicks), start, end))
                                / (double)seconds,
                            "checkpoints/second",
                            Reason: "Committed checkpoints by their database completion stamp."
                        ),
                ["primary_latency_p99"] = (admissionPath ? admissionLatency.Percentile(0.99) : p99) is { } primaryP99
                    ? new(
                        primaryP99,
                        "milliseconds",
                        primarySamples < minimumSamples ? "LOW_SAMPLE" : "MEASURED",
                        primarySamples < minimumSamples
                            ? $"Fewer than {minimumSamples} primary latency observations; retained with sample qualification."
                            : null
                    )
                    : Metric.Missing("milliseconds", "No primary latency samples."),
                ["clock_round_trip"] = new(
                    Ms(clock.RoundTripTicks),
                    "milliseconds",
                    Reason: "Round trip of the read that paired the database clock with the stopwatch; completion instants are exact to half of it."
                ),
                ["offered_rate"] =
                    drainPath
                        ? Metric.Missing(
                            "roots/second",
                            "Preloaded backlog has no offered arrival rate; cohort throughput includes worker startup."
                        )
                    : seconds == 0 ? Metric.Missing("roots/second", "Verification trial has no scheduled arrival rate.")
                    : new(offeredRate, "roots/second"),
                ["window_completed_rate"] =
                    seconds == 0
                        ? Metric.Missing("roots/second", "Verification trial has no fixed measurement window.")
                        : new(windowCompleted / (double)seconds, "roots/second"),
                ["end_backlog"] = new(backlog, "roots"),
                ["latency_max"] =
                    snapshot.Count == 0
                        ? Metric.Missing("milliseconds", "No observed completions.")
                        : new(snapshot.MaximumMs, "milliseconds"),
                ["backlog_slope"] = backlogSlope is { } slope
                    ? new(
                        slope,
                        "roots/second",
                        Reason: "OLS of one-second block means of the backlog rebuilt from acknowledgements and database completion stamps; incomplete blocks are excluded."
                    )
                    : Metric.Missing("roots/second", "Fewer than two sufficiently observed one-second backlog blocks."),
                ["backlog_samples"] = new(measuredBacklog.Length, "samples"),
                ["backlog_slope_tolerance"] = new(
                    SlopeTolerance(offeredRate, seconds),
                    "roots/second",
                    Reason: "Declared offered-rate fraction with a one-logical-root resolution floor over the measurement interval."
                ),
                ["database_cpu"] = Metric.Missing(
                    "cpu_seconds",
                    "Per-database CPU attribution is unavailable; whole-container CPU is reported separately where supported."
                ),
            };
            result["primary_rate"] = new(windowCompleted / (double)Math.Max(1, seconds), "roots/second");
            if (scenario.Id is "L02" or "L03")
            {
                var admitted = admissions.Values.Where(a => a.AcknowledgedTicks is not null).ToArray();
                var admissionDuration =
                    admitted.Length == 0
                        ? 0
                        : Ms(admitted.Max(a => a.AcknowledgedTicks!.Value) - admitted.Min(a => a.SubmittedTicks!.Value)) / 1000;
                result["primary_rate"] =
                    seconds == 0
                        ? Metric.Missing("admissions/second", "Untimed verification.")
                        : new(
                            admissions.Values.Count(a => a.AcknowledgedTicks is { } ack && Verdicts.InWindow(ack, start, end))
                                / (double)seconds,
                            "admissions/second"
                        );
                result["admission_rate"] =
                    admissionDuration > 0
                        ? new(admitted.Length / admissionDuration, "admissions/second")
                        : Metric.Missing("admissions/second", "No acknowledged admission interval.");
            }
            if (drainedAt is { } drainEnd && completionClock is [var firstAt, var lowerAt, var upperAt, var lastAt])
            {
                var drainDuration = Ms(drainEnd - start) / 1000;
                result["backlog_drain_rate"] = new(validRootKeys.Count / drainDuration, "roots/second");
                result["primary_rate"] = new(
                    validRootKeys.Count / drainDuration,
                    "backlog_roots/second",
                    Reason: "Cohort denominator includes drain and worker startup; this is not sustained open-arrival capacity."
                );
                result["steady_drain_rate"] = DrainCurve.SteadyRate(drainSamples, acknowledged, Stopwatch.Frequency) is { } steady
                    ? new(steady, "roots/second", Reason: "20% to 80% of the cohort, each crossing interpolated between terminal counts.")
                    : Metric.Missing("roots/second", "No terminal count before the 20% crossing to interpolate from.");
                result["db_steady_drain_rate"] =
                    upperAt > lowerAt
                        ? new(
                            0.6 * acknowledged * TimeSpan.TicksPerSecond / (upperAt - lowerAt),
                            "roots/second",
                            Reason: "20th to 80th percentile completion instant on the database clock."
                        )
                        : Metric.Missing("roots/second", "The 20th and 80th percentile completions share one database instant.");
                result["db_drain_seconds"] = new(
                    (lastAt - firstAt) / (double)TimeSpan.TicksPerSecond,
                    "seconds",
                    Reason: "First to last completion on the database clock."
                );
                result["first_completion_seconds"] = new(
                    Ms(DrainCurve.Reached(drainSamples, 1)!.Value - start) / 1000,
                    "seconds",
                    Reason: "Drain start to the first terminal count above zero, including worker startup."
                );
            }
            else if (drainPath)
            {
                result["primary_rate"] = Metric.Missing("backlog_roots/second", "The terminal count never reached the cohort.");
            }
            if (scenario.Id == "E02")
            {
                result["query_batches"] = new(queryLatency.Snapshot().Count, "batches");
                result["query_p95"] = queryLatency.Percentile(0.95) is { } value
                    ? new(value, "milliseconds")
                    : Metric.Missing("milliseconds", "No query samples.");
            }
            if (scenario.Id == "E03")
            {
                result["overload_rate"] = new(overloadRate, "roots/second");
                result["recovery_rate"] = new(recoveryRate, "roots/second");
                result["overload_backlog_slope"] = overloadSlope is { } growth
                    ? new(growth, "roots/second")
                    : Metric.Missing("roots/second", "Fewer than two sufficiently observed one-second backlog blocks.");
                result["recovery_backlog_threshold"] = new(recoveryThreshold, "roots");
            }
            foreach (var quantile in new[] { 0.5, 0.95, 0.99 })
            {
                var value = scheduledLatency.Percentile(quantile);
                result[$"latency_p{quantile * 100:F0}"] = value is null
                    ? Metric.Missing("milliseconds", "No observed completions.")
                    : new(
                        value,
                        "milliseconds",
                        snapshot.Count < minimumSamples && quantile == 0.99 ? "LOW_SAMPLE" : "MEASURED",
                        snapshot.Count < minimumSamples && quantile == 0.99 ? $"Below {minimumSamples} completed samples." : null
                    );
            }
            return result;
        }

        double SlopeTolerance(double rate, int windowSeconds) =>
            Math.Max(
                rate * plan.Policy("capacity", "backlog_slope_tolerance_fraction_of_offered_rate"),
                windowSeconds > 0 ? plan.Policy("capacity", "backlog_slope_resolution_floor_roots_per_interval") / windowSeconds : 0
            );

        Metric Worker(Func<WorkerSettings, double> setting, string unit) =>
            workers.Settings is { } settings
                ? new(setting(settings), unit)
                : Metric.Missing(unit, "No worker reported its resolved settings.");
    }

    private static EnqueueJobRequest Request(BenchWork input, string scenario, int index, string jobNamespace = "bench") =>
        new(
            jobNamespace,
            "work",
            JobPayload.Json(input, WorkJson.Default.BenchWork),
            DeduplicationKey: input.Key,
            Lane: scenario == "O01" ? $"lane-{index % 1000}"
                : scenario == "O02" ? "hot"
                : null,
            ConcurrencyKey: scenario == "O03" ? "hot" : null
        );

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    internal static bool CohortFitsBudget(string caseId, long roots, TrialCell? cell = null)
    {
        var logicalMultiplier =
            caseId is "P01" or "P08" ? 11
            : caseId is "M03" or "E01" ? 3
            : 1;
        var payloadBytes = cell?.PayloadBytes ?? (caseId == "M04" ? 65536 : 0);
        var estimatedBytes = roots * (logicalMultiplier * 2048.0 + payloadBytes * 2.0 + (caseId == "C01" ? 3072 : 0));
        return estimatedBytes <= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.25;
    }
}
