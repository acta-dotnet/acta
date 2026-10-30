using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

/// <summary>
/// A verification trial: submits a few jobs of a scenario's work, waits for every one, and reconciles each whole tree,
/// children and checkpoints included, against its model.
/// </summary>
internal static class TrialRunner
{
    /// <summary>
    /// Runs <paramref name="run"/>'s scenario once. Its file writes the jobs to submit (the harness's verification count
    /// otherwise) and, under <c>send_signal</c>, whether the controller raises each job's signal before any worker runs
    /// (<c>before-wait</c>) or once the job waits (<c>after-wait</c>), <c>signal_copies</c> times.
    /// </summary>
    public static async Task<CaseResult> VerifyAsync(ScenarioRun run, int repetition)
    {
        var (plan, scenario, environment, bundle, ct) = (run.Plan, run.Scenario, run.Environment, run.Bundle, run.Ct);
        var tag = $"{environment.Provider}-{scenario.Id}-verify-{repetition}-0";
        var trialStarted = Stopwatch.GetTimestamp();
        Console.WriteLine($"  START {tag}");
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var effects = scenario.NeedsEffectService ? new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db")) : null;
        if (effects is not null)
        {
            await effects.StartAsync(ct);
        }
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        bundle.Write($"histories/{tag}-settings.json", await state.CaptureSettingsAsync(ct), VerifyJson.Default.DictionaryStringString);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var reader = new DurableReader(state);
        var count = ScenarioRunner.VerificationJobs(plan, scenario, environment.Provider);
        var input = Enumerable.Range(0, count).Select(i => scenario.WorkAt(i, plan.Seed)).ToArray();
        var signals = input.ToDictionary(i => i.Key, i => i.Signal, StringComparer.Ordinal);
        var expected = WorkloadModel.Expected(input);
        var expectedRoots = expected.Where(e => e.Parent is null).ToDictionary(e => e.Key, StringComparer.Ordinal);
        var sendSignal = scenario.SettingText("send_signal", environment.Provider);
        var copies = (int)(scenario.SettingOrNull("signal_copies", environment.Provider) ?? 1);
        using var workers = new OwnedWorkerPool();
        // A signal raised before the wait must land before any worker could register that wait.
        var delayWorkers = sendSignal == "before-wait";
        if (!delayWorkers)
        {
            await workers.StartAsync(state, effects, 1, null, ct, 1, scenario.CrossNamespace);
        }
        var floor = await reader.LastJobIdAsync(ct);
        Dictionary<string, long> acknowledged = new(StringComparer.Ordinal);
        HashSet<string> ambiguous = new(StringComparer.Ordinal);
        HashSet<string> failedSignals = new(StringComparer.Ordinal);
        List<Check> boundaryChecks = [];
        for (var i = 0; i < input.Length; i++)
        {
            try
            {
                var accepted = await jobs.EnqueueAsync(Request(scenario, input[i], i), ct);
                acknowledged[input[i].Key] = accepted.JobId;
                if (input[i].Signal is { } signal && sendSignal == "before-wait")
                {
                    try
                    {
                        await RaiseAsync(jobs, accepted, signal, copies, ct);
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
        // An enqueue whose reply was lost may still have committed: its identity decides, by its deduplication key.
        foreach (var key in ambiguous.ToArray())
        {
            if (await jobs.GetAsync(JobLookup.ByDeduplicationKey("verify", key), ct) is { } committed)
            {
                acknowledged[key] = committed.JobId;
                ambiguous.Remove(key);
            }
        }
        if (delayWorkers)
        {
            await workers.StartAsync(state, effects, 1, null, ct, 1, scenario.CrossNamespace);
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
                if (sendSignal == "after-wait")
                {
                    foreach (var row in await reader.ReadAsync(null, ct, floor))
                    {
                        if (row.Status == 20 && signals.GetValueOrDefault(row.Key) is { } signal && signalled.Add(row.JobId))
                        {
                            await RaiseAsync(jobs, JobLookup.ById(row.JobId), signal, copies, ct);
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
            .. OutcomeCheck.Reconcile(expected.Where(e => submitted.Contains(e.Key.Split('/')[0])), observed),
            .. boundaryChecks,
            .. effects is null
                ? []
                : await effects.CheckIdempotentKeysAsync(
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
        var failures = OutcomeCheck.SafetyFailures(checks, unresolvedKeys, acknowledged.ContainsKey);
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
            "verify",
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

    private static async Task RaiseAsync(IJobs jobs, JobLookup job, string signal, int copies, CancellationToken ct)
    {
        for (var copy = 0; copy < copies; copy++)
        {
            await jobs.RaiseSignalAsync(job, signal, default(JobPayload), ct: ct);
        }
    }

    private static EnqueueJobRequest Request(Scenario scenario, WorkInput input, int index) =>
        new(
            "verify",
            "work",
            JobPayload.Json(input, WorkJson.Default.WorkInput),
            DeduplicationKey: input.Key,
            Lane: scenario.LaneAt(index),
            ConcurrencyKey: scenario.ConcurrencyKey
        );

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
