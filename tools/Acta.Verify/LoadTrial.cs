using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

/// <summary>One stretch of an open-loop arrival stream at a constant rate.</summary>
internal readonly record struct LoadSegment(double Seconds, double Rate);

/// <summary>
/// How a measured trial offers its jobs: a stream that workers serve as it arrives, a stream admitted before any worker
/// starts, or a cohort enqueued at once before any worker starts and then drained.
/// </summary>
internal enum LoadMode
{
    Stream,
    Admission,
    Cohort,
}

/// <summary>
/// A measured trial: its arrival schedule or cohort, its workload by arrival index, its topology and the target it is
/// judged by.
/// </summary>
internal sealed record LoadSpec(
    string CaseId,
    string Phase,
    int Repetition,
    double OfferedRate,
    LoadMode Mode,
    IReadOnlyList<LoadSegment> Segments,
    int Cohort,
    int WarmupSeconds,
    int WindowSeconds,
    int Calls,
    int JobsPerCall,
    int Workers,
    int? Executors,
    int Namespaces,
    double LatencyTargetMs,
    Func<int, WorkInput> Work,
    Func<int, string?> Lane,
    string? ConcurrencyKey,
    bool NeedsEffectService = false,
    bool CrossNamespace = false,
    double QueriesPerSecond = 0,
    int? PayloadBytes = null,
    double OverloadRate = 0,
    Func<IJobs, OwnedProvider, ResourceSampler, CancellationToken, Task<int>>? PrepareBackground = null,
    Func<OwnedProvider, int, CancellationToken, Task<Check[]>>? CheckBackground = null
);

/// <summary>
/// Runs one measured trial and judges it by its database completion stamps. The controller holds no job data: an
/// input is rebuilt from its arrival index when it is enqueued and again when its row is verified, each arrival keeps
/// one outcome byte and its job id, and verification streams the rows a page at a time. Nothing reaches the evidence
/// bundle between the stream's start and the drain's end.
/// </summary>
internal static class LoadTrial
{
    private const byte NotSent = 0;
    private const byte Acknowledged = 1;
    private const byte Ambiguous = 2;
    private const byte Recovered = 3;

    /// <summary>The failing identities a trial keeps in its evidence; the rest are counted.</summary>
    private const int FailureCap = 1000;

    /// <summary>The width of the completion buckets that the backlog and the drain percentiles are rebuilt from.</summary>
    private static readonly long BucketTicks = Stopwatch.Frequency / 100;

    /// <summary>The deduplication key of arrival <paramref name="index"/>, which is also its root's key.</summary>
    internal static string Key(int index) => $"root-{index}";

    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        OwnedProvider environment,
        EvidenceBundle bundle,
        LoadSpec spec,
        CancellationToken ct
    )
    {
        var rate = spec.OfferedRate;
        var cohort = spec.Mode == LoadMode.Cohort;
        var tag = $"{environment.Provider}-{spec.CaseId}-{spec.Phase}-{spec.Repetition}-{(int)rate}";
        var trialStarted = Stopwatch.GetTimestamp();
        var writeSecondsAtStart = bundle.WriteSeconds;
        Console.WriteLine($"  START {tag}");
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var effects = spec.NeedsEffectService ? new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db")) : null;
        if (effects is not null)
        {
            await effects.StartAsync(ct);
        }
        using var host = await RuntimeHost.StartAsync(
            state.Provider,
            state.EnqueueConnectionString(spec.Calls),
            state.Schema,
            false,
            true,
            ct
        );
        bundle.Write($"histories/{tag}-settings.json", await state.CaptureSettingsAsync(ct), VerifyJson.Default.DictionaryStringString);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var reader = new DurableReader(state);
        // The schedule after the stream's start: each segment's first arrival index and its offset in seconds.
        var segmentFirst = new int[spec.Segments.Count + 1];
        var segmentOffset = new double[spec.Segments.Count + 1];
        for (var s = 0; s < spec.Segments.Count; s++)
        {
            segmentFirst[s + 1] = segmentFirst[s] + (int)Math.Round(spec.Segments[s].Seconds * spec.Segments[s].Rate);
            segmentOffset[s + 1] = segmentOffset[s] + spec.Segments[s].Seconds;
        }
        var count = cohort ? spec.Cohort : segmentFirst[^1];
        if (count < 1)
        {
            throw new ArgumentException("A measured trial offers at least one job.");
        }
        var perRoot = 1 + Enumerable.Range(0, Math.Min(count, 5)).Max(i => WorkloadModel.Totals(spec.Work(i)).Descendants);
        var outcome = new byte[count];
        var ids = new long[count];
        using var workers = new OwnedWorkerPool();
        if (spec.Mode == LoadMode.Stream)
        {
            await workers.StartAsync(state, effects, spec.Workers, spec.Executors, ct, spec.Namespaces, spec.CrossNamespace);
        }
        await using var resources = new ResourceSampler(state, bundle, tag, workers.ProcessIds, ct);
        if (effects is not null)
        {
            resources.AddProcess(effects.ProcessId, "external_oracle");
        }
        var seeding = Stopwatch.GetTimestamp();
        var background = spec.PrepareBackground is null ? 0 : await spec.PrepareBackground(jobs, state, resources, ct);
        var backgroundTicks = Stopwatch.GetTimestamp() - seeding;
        // A cohort checkpoints after its enqueue instead.
        var checkpointed = !cohort && await state.CheckpointAsync(ct);
        var clock = await reader.ClockAsync(ct);
        var floor = await reader.LastJobIdAsync(ct);
        var streamStart = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        var start = streamStart + spec.WarmupSeconds * Stopwatch.Frequency;
        var end = spec.WindowSeconds == 0 ? long.MaxValue : start + spec.WindowSeconds * Stopwatch.Frequency;
        var lateTicks = (long)(plan.GeneratorLagToleranceMs * Stopwatch.Frequency / 1000);
        var progressMs = (int)plan.Policy("measurement", "progress_count_ms");
        var progressTicks = progressMs * Stopwatch.Frequency / 1000;
        var arrivals = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var calls = Enumerable.Range(0, spec.Calls).Select(_ => (Admission: new LatencyHistogram(), Lag: new LatencyHistogram())).ToArray();
        var liveChecks = new ConcurrentQueue<Check>();
        var queryLatency = new LatencyHistogram();
        List<DrainSample> drainSamples = [];
        long dispatched = 0,
            measuredArrivals = 0,
            acknowledged = 0,
            windowAcknowledged = 0,
            harnessLate = 0,
            wakeGapMax = 0,
            firstId = 0,
            lastId = 0,
            held = 0,
            drainStart = 0,
            drainEnded = 0;
        var lowest = floor + 1;
        var workerStartupMs = 0.0;
        var drained = false;
        var budgetExceeded = false;
        Thread? dispatcher = null;
        var observer = Task.CompletedTask;
        var callTasks = Array.Empty<Task>();
        using (bundle.Quiet())
        {
            try
            {
                dispatcher = new Thread(Dispatch) { IsBackground = true, Name = "acta-verify-arrivals" };
                dispatcher.Start();
                callTasks = [.. calls.Select(c => Task.Run(() => CallAsync(c.Admission, c.Lag), stop.Token))];
                observer = ObserveAsync();
                await Task.WhenAll(callTasks);
                dispatcher.Join();
                held = Interlocked.Read(ref acknowledged);
                if (cohort)
                {
                    // A stream's lost replies are settled by its rows after the drain; a cohort's drain counts by id range,
                    // so a reply lost in its enqueue is settled by its deduplication key first.
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, count).Where(i => outcome[i] == Ambiguous),
                        new ParallelOptions { MaxDegreeOfParallelism = spec.Calls, CancellationToken = ct },
                        async (i, token) =>
                        {
                            if (await jobs.GetAsync(JobLookup.ByDeduplicationKey(Namespace(i), Key(i)), token) is { } found)
                            {
                                (outcome[i], ids[i]) = (Recovered, found.JobId);
                            }
                        }
                    );
                    (firstId, lastId, held) = Range();
                    if (held > 0 && await reader.RangeCountAsync(firstId, lastId, ct) is var inRange && inRange != held)
                    {
                        throw new InvalidOperationException(
                            $"The job-id range {firstId}..{lastId} of the {held} acknowledged roots holds {inRange} jobs; a count over it would include other rows."
                        );
                    }
                    checkpointed = await state.CheckpointAsync(ct);
                }
                drainStart = Stopwatch.GetTimestamp();
                if (spec.Mode != LoadMode.Stream)
                {
                    await workers.StartAsync(state, effects, spec.Workers, spec.Executors, ct, spec.Namespaces, spec.CrossNamespace);
                    workerStartupMs = Ms(Stopwatch.GetTimestamp() - drainStart);
                    foreach (var pid in workers.ProcessIds)
                    {
                        resources.AddProcess(pid, "worker");
                    }
                }
                drained = await plan.TryDrainAsync(
                    RemainingAsync,
                    left => plan.Deadline(left * perRoot, 0, state.DrainRate),
                    drainStart,
                    progressMs,
                    ct
                );
                drainEnded = Stopwatch.GetTimestamp();
            }
            catch (OperationCanceledException) when (budgetExceeded && !ct.IsCancellationRequested) { }
            finally
            {
                await stop.CancelAsync();
                arrivals.Writer.TryComplete();
                dispatcher?.Join();
                try
                {
                    await Task.WhenAll(callTasks.Append(observer));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        if (budgetExceeded)
        {
            throw new ResourceBudgetException("Declared memory/disk resource budget exceeded; evidence retained.");
        }
        var endClock = await reader.ClockAsync(ct);
        var drift = Math.Abs(Ms(clock.ToStopwatch(endClock.DatabaseTicks) - endClock.Ticks));
        var driftTolerance = 50 + Ms(clock.RoundTripTicks + endClock.RoundTripTicks) / 2;

        // One streamed read of the trial's roots verifies each against its regenerated input and times its completion.
        // Descendants and checkpoints are reconciled in total, the trees the roots imply against one aggregate read; the
        // verification trials reconcile each tree whole.
        var verifying = Stopwatch.GetTimestamp();
        var totals = reader.TreeTotalsAsync(floor, ct);
        var seen = new BitArray(count);
        var idempotent = new BitArray(count);
        var latency = new LatencyHistogram();
        var buckets = new long[Math.Max(1, (drainEnded - streamStart) / BucketTicks + 2)];
        List<Check> failures = [];
        var failureCount = 0;
        long windowCompleted = 0,
            completedRoots = 0,
            unresolved = 0,
            firstCompletion = long.MaxValue,
            lastCompletion = long.MinValue,
            expectedDescendants = 0,
            expectedSucceededDescendants = 0,
            expectedSteps = 0;
        await foreach (var page in reader.RootPagesAsync(floor, ct))
        {
            var works = new WorkInput?[page.Count];
            Parallel.For(0, page.Count, r => works[r] = Index(page[r].Key) is { } i && i < count ? spec.Work(i) : null);
            for (var r = 0; r < page.Count; r++)
            {
                var row = page[r];
                if (works[r] is not { } work || Index(row.Key) is not { } i)
                {
                    Fail("unexpected-root", row.Key, "A root above the trial's floor is not one of its arrivals.");
                    continue;
                }
                if (seen[i])
                {
                    Fail("unique-terminal", row.Key, "Two roots carry one arrival's identity.");
                    continue;
                }
                seen[i] = true;
                idempotent[i] = work.EffectMode == "idempotent";
                var (descendants, succeeded, steps) = WorkloadModel.Totals(work);
                (expectedDescendants, expectedSucceededDescendants, expectedSteps) = (
                    expectedDescendants + descendants,
                    expectedSucceededDescendants + succeeded,
                    expectedSteps + steps
                );
                if (outcome[i] is Acknowledged or Recovered && ids[i] != row.JobId)
                {
                    Fail("acknowledged-identity", row.Key, "The acknowledged job id does not identify this arrival's root.");
                }
                // A root whose enqueue threw yet committed is the durable truth: it counts as admitted.
                if (outcome[i] is NotSent or Ambiguous)
                {
                    (outcome[i], ids[i]) = (Recovered, row.JobId);
                }
                if (row.JobNamespace != Namespace(i))
                {
                    Fail("acknowledged-identity", row.Key, $"The root is in namespace {row.JobNamespace}, not {Namespace(i)}.");
                }
                if (row.Status is not (100 or 200 or 220))
                {
                    unresolved++;
                    Fail("unresolved", row.Key, "The root was not terminal once the drain ended.");
                    continue;
                }
                var completed = clock.ToStopwatch(row.Modified);
                var wanted = WorkloadModel.Root(work);
                var correct = DurableReader.Status(row.Status) == wanted.Status && row.Output == wanted.Output;
                if (!correct)
                {
                    Fail("observed-terminal-outcome", row.Key, "The terminal row does not hold the expected result and status.");
                }
                if (Measured(i))
                {
                    // A stamp mapped across the clocks can land a fraction of the pairing's round trip early.
                    latency.Record(Math.Max(0, Ms(completed - Scheduled(i))));
                }
                if (correct && wanted.Status == "Succeeded")
                {
                    completedRoots++;
                    windowCompleted += Verdicts.InWindow(completed, start, end) ? 1 : 0;
                }
                buckets[Math.Clamp((completed - streamStart) / BucketTicks, 0, buckets.Length - 1)]++;
                firstCompletion = Math.Min(firstCompletion, completed);
                lastCompletion = Math.Max(lastCompletion, completed);
            }
        }
        long refused = 0,
            recovered = 0;
        for (var i = 0; i < count; i++)
        {
            recovered += outcome[i] == Recovered ? 1 : 0;
            if (seen[i])
            {
                continue;
            }
            if (outcome[i] is Acknowledged or Recovered)
            {
                Fail("missing-identity", Key(i), "An acknowledged arrival has no durable root.");
            }
            else
            {
                refused++;
            }
        }
        var (actualDescendants, actualSucceeded, actualSteps) = await totals;
        if (
            unresolved == 0
            && refused == 0
            && (actualDescendants, actualSucceeded, actualSteps) != (expectedDescendants, expectedSucceededDescendants, expectedSteps)
        )
        {
            Fail(
                "tree-totals",
                "trial",
                $"The roots imply {expectedDescendants} descendants ({expectedSucceededDescendants} succeeded) and {expectedSteps} checkpoints; the trial holds {actualDescendants} ({actualSucceeded}) and {actualSteps}."
            );
        }
        foreach (var check in liveChecks)
        {
            Fail(check.Code, check.Identity, check.Detail);
        }
        var stored = await Task.WhenAll(
            spec.CheckBackground is null ? Task.FromResult<Check[]>([]) : spec.CheckBackground(state, background, ct),
            effects is null
                ? Task.FromResult<Check[]>([])
                : effects.CheckIdempotentKeysAsync(Enumerable.Range(0, count).Where(i => idempotent[i]).Select(Key), ct)
        );
        foreach (var check in stored.SelectMany(c => c).Where(c => !c.Passed))
        {
            Fail(check.Code, check.Identity, check.Detail);
        }
        var safety = failureCount - unresolved;

        // The backlog counts every scheduled arrival from its scheduled instant until its completion stamp, so a
        // stream that fell behind or an arrival that never committed stays inside it.
        var curve = new DrainSample[buckets.Length + 1];
        curve[0] = new(streamStart, 0);
        for (var b = 0; b < buckets.Length; b++)
        {
            curve[b + 1] = new(streamStart + (b + 1) * BucketTicks, curve[b].Completed + buckets[b]);
        }
        long opened = 0;
        var backlog =
            spec.Mode == LoadMode.Stream
                ? BacklogTrend.Reconstruct(ScheduledBy, curve, streamStart, drainEnded, progressTicks)
                : Array.Empty<BacklogSample>();
        var windowBacklog = backlog.Where(s => s.Timestamp >= start && s.Timestamp < end).ToArray();
        var slope = BacklogTrend.Slope(windowBacklog, Stopwatch.Frequency);
        var tolerance = Math.Max(
            rate * plan.Policy("capacity", "backlog_slope_tolerance_fraction_of_offered_rate"),
            spec.WindowSeconds > 0 ? plan.Policy("capacity", "backlog_slope_resolution_floor_roots_per_interval") / spec.WindowSeconds : 0
        );
        var admission = new LatencyHistogram();
        var lag = new LatencyHistogram();
        foreach (var call in calls)
        {
            admission.Add(call.Admission);
            lag.Add(call.Lag);
        }
        var admissionOnly = spec.Mode == LoadMode.Admission;
        var p99 = admissionOnly ? admission.Percentile(0.99) : latency.Percentile(0.99);
        // An unresolved root or an enqueue that ended without a reply or a row leaves latency censored.
        var observerLost = latency.Snapshot().Overflow > 0 || admission.Snapshot().Overflow > 0 || unresolved > 0 || refused > 0;
        var workerExit = workers.LiveCount != spec.Workers;
        var clockStepped = drift > driftTolerance;
        var measurement =
            spec.WindowSeconds == 0 && !cohort ? "NOT_MEASURED"
            : workerExit || clockStepped || spec.Mode == LoadMode.Stream && slope is null ? "UNAVAILABLE"
            : Verdicts.Measurement(
                measuredArrivals,
                harnessLate,
                observerLost,
                cohort ? 0 : p99 ?? double.PositiveInfinity,
                spec.LatencyTargetMs,
                spec.Mode == LoadMode.Stream ? slope ?? double.PositiveInfinity : 0,
                tolerance,
                plan.Policy("capacity", "max_generator_missed_fraction_for_valid_capacity")
            );

        // An overload trial must grow the backlog in its overload half and bring it under the threshold in its recovery half.
        string? overloadGap = null;
        if (spec.OverloadRate > 0)
        {
            var overloadEnd = start + spec.WindowSeconds / 2 * Stopwatch.Frequency;
            var growth = BacklogTrend.Slope(
                backlog.Where(t => t.Timestamp >= start && t.Timestamp < overloadEnd).ToArray(),
                Stopwatch.Frequency
            );
            var threshold = Math.Max(
                plan.Policy("capacity", "overload", "backlog_threshold_floor_roots"),
                spec.OverloadRate * plan.Policy("capacity", "overload", "backlog_threshold_fraction_of_overload_rate")
            );
            var saturated =
                growth
                > Math.Max(
                    spec.OverloadRate * plan.Policy("capacity", "overload", "saturation_backlog_slope_fraction_of_overload_rate"),
                    spec.WindowSeconds / 2 > 0
                        ? plan.Policy("capacity", "backlog_slope_resolution_floor_roots_per_interval") / (spec.WindowSeconds / 2)
                        : 0
                );
            var recoveredBelow = backlog.Any(t => t.Timestamp >= overloadEnd && t.Timestamp < end && t.Backlog < threshold);
            overloadGap =
                !saturated ? $"The offered overload of {spec.OverloadRate} roots/s did not grow the backlog during the overload half."
                : !recoveredBelow ? $"The backlog did not fall below {threshold} roots during the recovery half."
                : null;
        }
        var overloadGates = overloadGap is not null && plan.MeasurementGatesVerdict;
        var exercised = completedRoots + unresolved > 0;
        var metrics = Metrics();
        foreach (var (name, metric) in resources.Summary(completedRoots))
        {
            metrics[name] = metric;
        }
        metrics["setup_seconds"] = new(
            Seconds(streamStart - trialStarted - backgroundTicks),
            "seconds",
            Reason: "Schema, host, workers, checkpoint and clock pairing."
        );
        metrics["seed_seconds"] = new(
            Seconds(backgroundTicks + (cohort ? drainStart - streamStart : 0)),
            "seconds",
            Reason: "Background population and the cohort's enqueue."
        );
        metrics["window_seconds"] = new(
            Seconds(drainEnded - (cohort ? drainStart : streamStart)),
            "seconds",
            Reason: "The arrival stream and its drain."
        );
        metrics["verify_seconds"] = new(
            Seconds(Stopwatch.GetTimestamp() - verifying),
            "seconds",
            Reason: "The streamed read and its checks."
        );

        var status =
            safety > 0 || unresolved > 0 ? "FAIL"
            : refused > 0 || !state.SettingsVerified || overloadGates || workerExit || !exercised ? "INCONCLUSIVE"
            : "PASS";
        bundle.Write($"histories/{tag}-checks.json", failures.ToArray(), VerifyJson.Default.CheckArray);
        bundle.WriteLines(
            $"histories/{tag}-backlog.csv",
            backlog
                .Select(s => string.Create(CultureInfo.InvariantCulture, $"{Seconds(s.Timestamp - streamStart):R},{s.Backlog}"))
                .Prepend("elapsed_seconds,backlog")
        );
        List<string> evidence = [$"histories/{tag}-checks.json", $"histories/{tag}-window.json", $"histories/{tag}-backlog.csv"];
        if (cohort)
        {
            bundle.WriteLines(
                $"histories/{tag}-progress.csv",
                drainSamples
                    .Select(s =>
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{Seconds(s.Timestamp - drainStart):R},{s.Completed},{Seconds(s.QueryTicks):R}"
                        )
                    )
                    .Prepend("elapsed_seconds,completed,query_seconds")
            );
            evidence.Add($"histories/{tag}-progress.csv");
        }
        bundle.Write(
            $"histories/{tag}-window.json",
            new Dictionary<string, string>
            {
                ["stream_start_ticks"] = Invariant(streamStart),
                ["start_ticks"] = Invariant(start),
                ["end_ticks"] = Invariant(end),
                ["clock_frequency"] = Invariant(Stopwatch.Frequency),
                ["database_clock_ticks"] = Invariant(clock.Ticks),
                ["database_clock_database_ticks"] = Invariant(clock.DatabaseTicks),
                ["database_clock_round_trip_ticks"] = Invariant(clock.RoundTripTicks),
                ["floor_job_id"] = Invariant(floor),
                ["latency_origin"] = "Each arrival's scheduled instant to the database stamp of the transaction that completed it.",
                ["throughput_boundary"] = cohort
                    ? "Cohort drain after its enqueue, including worker startup."
                    : "Half-open fixed window after the stream's warmup; drain completions are excluded.",
            },
            VerifyJson.Default.DictionaryStringString
        );
        bundle.Write($"histograms/{tag}-scheduled.json", latency.Snapshot(), VerifyJson.Default.HistogramSnapshot);
        bundle.Write($"histograms/{tag}-admission.json", admission.Snapshot(), VerifyJson.Default.HistogramSnapshot);
        bundle.Write($"histograms/{tag}-generator-lag.json", lag.Snapshot(), VerifyJson.Default.HistogramSnapshot);
        metrics["evidence_seconds"] = new(bundle.WriteSeconds - writeSecondsAtStart, "seconds", Reason: "Evidence files the trial wrote.");
        return new(
            spec.CaseId,
            environment.Provider,
            spec.Phase,
            spec.Repetition,
            status,
            safety > 0 ? $"{failureCount} failed identity or outcome checks; the first {Math.Min(failureCount, FailureCap)} are kept."
                : unresolved > 0 ? "Accepted cohort remains unresolved."
                : refused > 0 ? $"{refused} enqueues ended without a reply and left no row; their fate is unknown."
                : overloadGates ? overloadGap
                : workerExit ? "An owned worker exited unexpectedly; this trial cannot establish clean performance."
                : !exercised ? "No submitted logical workload was observed in durable state; the required workload was not exercised."
                : clockStepped ? $"The database clock moved {drift:F0} ms against the stopwatch during the trial."
                : overloadGap is null ? null
                : Verdicts.Informational(plan, overloadGap),
            safety > 0 ? "FAIL"
                : unresolved > 0 || !exercised ? "UNVERIFIED"
                : "PASS",
            unresolved > 0 ? "FAIL"
                : !exercised ? "UNVERIFIED"
                : "PASS",
            !overloadGates && !workerExit && exercised && refused == 0 ? "COMPLETE" : "INCOMPLETE",
            measurement,
            metrics,
            evidence,
            "Acta native jobs/checkpoints/children; independent committed-state SQL observer"
        );

        bool Measured(int i) => spec.WarmupSeconds == 0 || Scheduled(i) >= start;

        // Batch arrivals share the instant of the first arrival in their batch.
        long Scheduled(int i)
        {
            if (cohort)
            {
                return streamStart;
            }
            var first = spec.JobsPerCall > 1 ? i - i % spec.JobsPerCall : i;
            var s = 0;
            while (s < spec.Segments.Count - 1 && first >= segmentFirst[s + 1])
            {
                s++;
            }
            var offset = segmentOffset[s] + (first - segmentFirst[s]) / Math.Max(1e-9, spec.Segments[s].Rate);
            return streamStart + (long)(offset * Stopwatch.Frequency);
        }

        // The arrivals scheduled by instant t; the backlog asks in rising order of t.
        long ScheduledBy(long t)
        {
            while (opened < count && Scheduled((int)opened) <= t)
            {
                opened++;
            }
            return opened;
        }

        string Namespace(int i) => i % spec.Namespaces == 0 ? "verify" : "verify-2";

        int? Index(string key) =>
            key.StartsWith("root-", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                ? i
                : null;

        EnqueueJobRequest Request(int i)
        {
            var work = spec.Work(i);
            return new(
                Namespace(i),
                "work",
                JobPayload.Json(work, WorkJson.Default.WorkInput),
                DeduplicationKey: work.Key,
                Lane: spec.Lane(i),
                ConcurrencyKey: spec.ConcurrencyKey
            );
        }

        (long First, long Last, long Held) Range()
        {
            var (low, high, n) = (long.MaxValue, long.MinValue, 0L);
            for (var i = 0; i < count; i++)
            {
                if (outcome[i] is Acknowledged or Recovered)
                {
                    (low, high, n) = (Math.Min(low, ids[i]), Math.Max(high, ids[i]), n + 1);
                }
            }
            return low > high ? (0, 0, 0) : (low, high, n);
        }

        void Fail(string code, string identity, string detail)
        {
            failureCount++;
            if (failures.Count < FailureCap)
            {
                failures.Add(new(code, identity, false, detail));
            }
        }

        // A dedicated thread hands each call's block, one arrival or a declared batch, to the calls at its scheduled instant,
        // never early, never dropping one. A block it hands over more than the tolerance late is the harness's lateness,
        // not the engine's: the calls were waiting on the dispatcher, not the other way round. A cohort is all due at once,
        // so its hand-over has no lateness to count.
        void Dispatch()
        {
            var next = 0;
            var wake = Stopwatch.GetTimestamp();
            try
            {
                while (next < count && !stop.IsCancellationRequested)
                {
                    var now = Stopwatch.GetTimestamp();
                    wakeGapMax = Math.Max(wakeGapMax, now - wake);
                    wake = now;
                    while (next < count && Scheduled(next) is var scheduled && scheduled <= Stopwatch.GetTimestamp())
                    {
                        var size = Math.Min(spec.JobsPerCall, count - next);
                        if (spec.WarmupSeconds == 0 || scheduled >= start)
                        {
                            measuredArrivals += size;
                            harnessLate += !cohort && Stopwatch.GetTimestamp() - scheduled > lateTicks ? size : 0;
                        }
                        arrivals.Writer.TryWrite(next);
                        next += size;
                    }
                    Volatile.Write(ref dispatched, next);
                    if (next < count)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                arrivals.Writer.TryComplete();
            }
        }

        // A block waits here for a free call when every call the trial allows is busy; that wait is the engine's, so it
        // flows into admission, latency and the backlog rather than into the harness's lateness. Each block wakes one call.
        async Task CallAsync(LatencyHistogram admissionLatency, LatencyHistogram callLag)
        {
            while (true)
            {
                int first;
                try
                {
                    first = await arrivals.Reader.ReadAsync(stop.Token);
                }
                catch (ChannelClosedException)
                {
                    return;
                }
                var size = Math.Min(spec.JobsPerCall, count - first);
                var scheduled = Scheduled(first);
                var measured = spec.WarmupSeconds == 0 || scheduled >= start;
                var submitted = Stopwatch.GetTimestamp();
                for (var k = 0; measured && k < size; k++)
                {
                    callLag.Record(Ms(submitted - scheduled));
                }
                try
                {
                    if (spec.JobsPerCall == 1)
                    {
                        ids[first] = (await jobs.EnqueueAsync(Request(first), stop.Token)).JobId;
                    }
                    else
                    {
                        var requests = new EnqueueJobRequest[size];
                        for (var k = 0; k < size; k++)
                        {
                            requests[k] = Request(first + k);
                        }
                        var response = await jobs.EnqueueBatchAsync(requests, stop.Token);
                        for (var k = 0; k < size; k++)
                        {
                            ids[first + k] = response.Jobs[k].JobId;
                        }
                    }
                    var acked = Stopwatch.GetTimestamp();
                    if (Verdicts.InWindow(acked, start, end))
                    {
                        Interlocked.Add(ref windowAcknowledged, size);
                    }
                    for (var k = 0; k < size; k++)
                    {
                        outcome[first + k] = Acknowledged;
                        if (measured)
                        {
                            admissionLatency.Record(Ms(acked - scheduled));
                        }
                    }
                    Interlocked.Add(ref acknowledged, size);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    for (var k = 0; k < size; k++)
                    {
                        outcome[first + k] = Ambiguous;
                    }
                }
            }
        }

        async Task ObserveAsync()
        {
            var token = stop.Token;
            var nextProgress = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            var nextLive = Stopwatch.GetTimestamp();
            var nextQuery = nextLive;
            HashSet<long> live = [];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var cycle = Stopwatch.GetTimestamp();
                    if (resources.BudgetExceeded)
                    {
                        budgetExceeded = true;
                        await stop.CancelAsync();
                        return;
                    }
                    if (perRoot > 1 && cycle >= nextLive)
                    {
                        foreach (var check in await reader.LiveChildChecksAsync(floor, live, token))
                        {
                            liveChecks.Enqueue(check);
                        }
                        nextLive = cycle + progressTicks;
                    }
                    if (spec.QueriesPerSecond > 0 && cycle >= nextQuery)
                    {
                        var operations = host.Services.GetRequiredService<IActaOperations>();
                        await operations.Ledger.ListJobsAsync(new(JobNamespace: "verify", PageSize: 20), token);
                        await operations.Ledger.ListEventsAsync(new(JobNamespace: "verify", PageSize: 20), token);
                        queryLatency.Record(Ms(Stopwatch.GetTimestamp() - cycle));
                        nextQuery += (long)(Stopwatch.Frequency / spec.QueriesPerSecond);
                    }
                    if (cycle >= nextProgress)
                    {
                        Console.WriteLine(
                            $"  {environment.Provider} {spec.CaseId} {spec.Phase} trial {spec.Repetition} | {Seconds(Math.Max(0, cycle - streamStart)):F0}s | scheduled {Volatile.Read(ref dispatched)}/{count} | acknowledged {Interlocked.Read(ref acknowledged)}"
                        );
                        nextProgress = cycle + 5 * Stopwatch.Frequency;
                    }
                    await Task.Delay(plan.ObserverCadenceMs, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        // One aggregate count a sample: a cohort through the claim and in-flight indexes over its id range, a stream over
        // its roots above the floor.
        async Task<long> RemainingAsync()
        {
            var queried = Stopwatch.GetTimestamp();
            long left;
            if (cohort)
            {
                left = await reader.RemainingCountAsync(firstId, lastId, ct);
            }
            else
            {
                (left, lowest) = await reader.UnfinishedRootsAsync(lowest, ct);
            }
            var counted = Stopwatch.GetTimestamp();
            drainSamples.Add(new(counted, held - left, counted - queried));
            return left;
        }

        Dictionary<string, Metric> Metrics()
        {
            var minimumSamples = plan.Policy("measurement", "minimum_p99_samples");
            var primarySamples = admissionOnly ? admission.Snapshot().Count : latency.Snapshot().Count;
            var cohortRate = completedRoots / Math.Max(1e-9, Seconds(drainEnded - drainStart));
            var result = new Dictionary<string, Metric>(StringComparer.Ordinal)
            {
                ["executors_per_worker"] = Worker(s => s.Executors, "executors"),
                ["worker_heartbeat"] = Worker(s => s.HeartbeatSeconds, "seconds"),
                ["worker_lease"] = Worker(s => s.LeaseSeconds, "seconds"),
                ["worker_safety_poll"] = Worker(s => s.SafetyPollMs, "milliseconds"),
                ["worker_startup"] = new(workerStartupMs, "milliseconds"),
                ["batch_size"] = new(spec.JobsPerCall, "roots/call"),
                ["enqueue_calls"] = new(spec.Calls, "calls", Reason: "Enqueue calls the controller keeps in flight at most."),
                ["worker_processes"] = new(spec.Workers, "processes"),
                ["live_worker_processes_at_reconciliation"] = new(workers.LiveCount, "processes"),
                ["namespace_count"] = new(spec.Namespaces, "namespaces"),
                ["payload_bytes"] = spec.PayloadBytes is { } payloadBytes
                    ? new(payloadBytes, "bytes")
                    : Metric.Missing("bytes", "The scenario mixes payload sizes or writes none."),
                ["background_jobs"] = new(background, "jobs"),
                ["checkpoint_before_window"] = new(checkpointed ? 1 : 0, "boolean"),
                ["warmup_seconds"] = new(spec.WarmupSeconds, "seconds"),
                ["scheduled_window_seconds"] = new(spec.WindowSeconds, "seconds"),
                ["intended_roots"] = new(count, "roots"),
                ["acknowledged_roots"] = new(Interlocked.Read(ref acknowledged), "roots"),
                ["ambiguous_admissions"] = new(recovered + refused, "roots"),
                ["reconciled_ambiguous_admissions"] = new(recovered, "roots"),
                ["refused_arrivals"] = new(refused, "roots", Reason: "Enqueues that ended without a reply and left no row."),
                ["unresolved_roots"] = new(unresolved, "roots"),
                ["missed_arrivals"] = new(0, "roots", Reason: "Arrivals wait for a call; none is dropped."),
                ["generator_late_arrivals"] = new(
                    harnessLate,
                    "roots",
                    Reason: "Arrivals the dispatcher handed over later than the tolerance: harness lateness."
                ),
                ["generator_lag_tolerance"] = new(plan.GeneratorLagToleranceMs, "milliseconds"),
                ["dispatcher_wake_max"] = new(Ms(wakeGapMax), "milliseconds", Reason: "Longest gap between two dispatcher wakes."),
                ["generator_lag_p99"] = lag.Percentile(0.99) is { } lagP99
                    ? new(lagP99, "milliseconds", Reason: "Scheduled instant to submission, including the wait for a free call.")
                    : Metric.Missing("milliseconds", "No submissions."),
                ["admission_p50"] = admission.Percentile(0.5) is { } admissionP50
                    ? new(admissionP50, "milliseconds")
                    : Metric.Missing("milliseconds", "No acknowledged admissions."),
                ["admission_p99"] = admission.Percentile(0.99) is { } admissionP99
                    ? new(admissionP99, "milliseconds")
                    : Metric.Missing("milliseconds", "No acknowledged admissions."),
                ["latency_samples"] = new(latency.Snapshot().Count, "roots"),
                ["primary_latency_p99"] = p99 is { } primaryP99
                    ? new(
                        primaryP99,
                        "milliseconds",
                        primarySamples < minimumSamples ? "LOW_SAMPLE" : "MEASURED",
                        primarySamples < minimumSamples ? $"Fewer than {minimumSamples} primary latency observations." : null
                    )
                    : Metric.Missing("milliseconds", "No primary latency samples."),
                ["clock_round_trip"] = new(Ms(clock.RoundTripTicks), "milliseconds"),
                ["clock_drift"] = new(drift, "milliseconds", Reason: "Database clock against the stopwatch, trial start to end."),
                ["offered_rate"] = cohort
                    ? Metric.Missing("roots/second", "A cohort has no offered arrival rate; its drain includes worker startup.")
                    : new(rate, "roots/second"),
                ["window_completed_rate"] =
                    spec.WindowSeconds == 0
                        ? Metric.Missing("roots/second", "No fixed measurement window.")
                        : new(windowCompleted / (double)spec.WindowSeconds, "roots/second"),
                ["primary_rate"] =
                    cohort && drained && completedRoots > 0
                        ? new(
                            cohortRate,
                            "backlog_roots/second",
                            Reason: "Cohort denominator includes drain and worker startup; this is not sustained open-arrival capacity."
                        )
                    : cohort ? Metric.Missing("backlog_roots/second", "The cohort never finished draining.")
                    : admissionOnly ? new(windowAcknowledged / (double)Math.Max(1, spec.WindowSeconds), "admissions/second")
                    : new(windowCompleted / (double)Math.Max(1, spec.WindowSeconds), "roots/second"),
                ["latency_max"] =
                    latency.Snapshot().Count == 0
                        ? Metric.Missing("milliseconds", "No observed completions.")
                        : new(latency.Snapshot().MaximumMs, "milliseconds"),
                ["backlog_slope"] = slope is { } value
                    ? new(
                        value,
                        "roots/second",
                        Reason: "OLS of one-second block means of the backlog rebuilt from scheduled arrivals and database completion stamps."
                    )
                    : Metric.Missing("roots/second", "Fewer than two sufficiently observed one-second backlog blocks."),
                ["backlog_samples"] = new(windowBacklog.Length, "samples"),
                ["backlog_slope_tolerance"] = new(tolerance, "roots/second"),
            };
            foreach (var quantile in new[] { 0.5, 0.95, 0.99 })
            {
                result[$"latency_p{quantile * 100:F0}"] = latency.Percentile(quantile) is { } q
                    ? new(q, "milliseconds")
                    : Metric.Missing("milliseconds", "No observed completions.");
            }
            if (cohort && drained && completedRoots > 0)
            {
                result["backlog_drain_rate"] = new(cohortRate, "roots/second");
                result["db_steady_drain_rate"] = DrainCurve.SteadyRate(curve, completedRoots, Stopwatch.Frequency) is { } dbSteady
                    ? new(dbSteady, "roots/second", Reason: "20th to 80th percentile completion stamp on the database clock.")
                    : Metric.Missing("roots/second", "The 20th and 80th percentile completions share one bucket.");
                result["steady_drain_rate"] = DrainCurve.SteadyRate(drainSamples, completedRoots, Stopwatch.Frequency) is { } steady
                    ? new(steady, "roots/second", Reason: "20% to 80% of the cohort by the controller's terminal counts.")
                    : Metric.Missing("roots/second", "No terminal count before the 20% crossing to interpolate from.");
                result["db_drain_seconds"] = new(
                    Seconds(lastCompletion - firstCompletion),
                    "seconds",
                    Reason: "First to last completion on the database clock."
                );
                result["first_completion_seconds"] = new(
                    Seconds(firstCompletion - drainStart),
                    "seconds",
                    Reason: "Drain start to the first completion, including worker startup."
                );
            }
            if (spec.QueriesPerSecond > 0)
            {
                result["query_batches"] = new(queryLatency.Snapshot().Count, "batches");
                result["query_p95"] = queryLatency.Percentile(0.95) is { } q95
                    ? new(q95, "milliseconds")
                    : Metric.Missing("milliseconds", "No query samples.");
            }
            if (spec.OverloadRate > 0)
            {
                result["overload_rate"] = new(spec.OverloadRate, "roots/second");
                result["recovery_rate"] = new(spec.Segments[^1].Rate, "roots/second");
            }
            return result;
        }

        Metric Worker(Func<WorkerSettings, double> setting, string unit) =>
            workers.Settings is { } workerSettings
                ? new(setting(workerSettings), unit)
                : Metric.Missing(unit, "No worker reported its resolved settings.");
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
