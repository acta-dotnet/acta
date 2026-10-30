using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class FaultCaseRunner
{
    /// <summary>The scenarios that kill a worker holding the work they watch, so recovery must reclaim an attempt.</summary>
    public static bool RequiresReclaim(string id) =>
        id
            is "chaos"
                or "crash-after-checkpoint"
                or "crash-after-effect"
                or "at-most-once"
                or "parent-dies-during-fan-out"
                or "kill-busy-worker"
                or "kill-all-workers"
                or "non-idempotent-sink"
                or "idempotent-sink-crash";

    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        Scenario scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        int repetition,
        CancellationToken ct
    )
    {
        if (scenario.Id == "database-crash" && !environment.OwnsDatabaseProcess)
        {
            await using var crashDatabase = new OwnedProvider(
                environment.Provider,
                bundle.Resolve($"repro/state/{environment.Provider}-database-crash-owned-{repetition}")
            );
            await crashDatabase.ProvisionAsync(plan, useComposeServer: false, ct);
            var settings = await crashDatabase.CaptureSettingsAsync(ct);
            foreach (var (key, value) in crashDatabase.ServerSpecs)
            {
                settings[key] = value;
            }
            bundle.Write(
                $"histories/{environment.Provider}-database-crash-owned-{repetition}-settings.json",
                settings,
                VerifyJson.Default.DictionaryStringString
            );
            var result = await LedgerValidation.ApplyAsync(
                await RunAsync(plan, scenario, crashDatabase, bundle, repetition, ct),
                crashDatabase.TakeTrials(),
                bundle,
                ct
            );
            crashDatabase.RetainState = result.Status != "PASS";
            return result;
        }
        var tag = $"{environment.Provider}-{scenario.Id}-fault-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        using var effects = new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db"));
        await effects.StartAsync(ct);
        var input = scenario.Work[0].ToInput("root", 0, plan.Seed);
        var sideInputs = scenario.Id is "kill-busy-worker" or "kill-all-workers" or "database-crash" or "rolling-restart"
            ? Enumerable.Range(0, 32).Select(i => new WorkInput($"side-{i}", WorkMs: 1000)).ToArray()
            : [];
        var expected = WorkloadModel.Expected(new[] { input }.Concat(sideInputs));
        if (scenario.Id == "at-most-once")
        {
            expected[0] = expected[0] with { Status = "Failed", Output = null, Steps = ["effect=uncommitted"] };
        }
        if (scenario.Id == "cancel-descendants")
        {
            expected = expected
                .Where(e => e.Parent is null || Enumerable.Range(0, 4).Any(i => e.Key == $"root/child-{i}"))
                .Select(e => e.Parent is null ? e with { Status = "Cancelled", Output = null } : e with { Status = "CancelledOrSucceeded" })
                .ToArray();
        }
        List<OwnedWorker> workers = [];
        List<Check> checks = [];
        FaultEvidence? fault = null;
        var transientReadRetries = 0;
        var bound = 0;
        var observingRecovery = false;
        try
        {
            var worker = OwnedWorker.Start(state, effects);
            workers.Add(worker);
            await worker.ReadyAsync(ct);
            bound = plan.Deadline(expected.Length, worker.Settings!.LeaseSeconds, environment.DrainRate);
            if (scenario.Id is "kill-busy-worker" or "kill-all-workers" or "rolling-restart")
            {
                var survivor = OwnedWorker.Start(state, effects);
                workers.Add(survivor);
                await survivor.ReadyAsync(ct);
            }
            var root = await jobs.EnqueueAsync(
                new EnqueueJobRequest("verify", "work", JobPayload.Json(input, WorkJson.Default.WorkInput), DeduplicationKey: input.Key),
                ct
            );
            foreach (var side in sideInputs)
            {
                await jobs.EnqueueAsync(
                    new EnqueueJobRequest("verify", "work", JobPayload.Json(side, WorkJson.Default.WorkInput), DeduplicationKey: side.Key),
                    ct
                );
            }
            var reader = new DurableReader(state);
            if (scenario.Id == "durable-timer")
            {
                await WaitAsync(
                    async () =>
                        (await jobs.GetCheckpointsAsync(root, ct))?.Items.Any(c => c.Name == "sleep" && c.DueAtUtc is not null) == true,
                    bound,
                    ct
                );
                var armed = (await jobs.GetCheckpointsAsync(root, ct))?.Items.FirstOrDefault(c => c.Name == "sleep");
                var armedBeforeDue = armed?.DueAtUtc > DateTimeOffset.UtcNow;
                checks.Add(new("timer-armed", input.Key, armed?.DueAtUtc is not null, "Durable timer existed before restart."));
                var killed = worker.Kill();
                fault = new(
                    "durable-timer-worker-restart",
                    [input.Key],
                    armedBeforeDue,
                    killed,
                    false,
                    RecordedAtUtc: DateTimeOffset.UtcNow
                );
                var replacement = OwnedWorker.Start(state, effects);
                workers.Add(replacement);
                await replacement.ReadyAsync(ct);
                observingRecovery = true;
                await WaitAsync(async () => (await jobs.GetAsync(root, ct))?.Status.IsTerminal == true, bound, ct);
                var timer = (await jobs.GetCheckpointsAsync(root, ct))?.Items.FirstOrDefault(c => c.Name == "sleep");
                checks.Add(
                    new(
                        "timer-not-early",
                        input.Key,
                        timer is not null && timer.ModifiedAtUtc >= timer.DueAtUtc,
                        "Timer consumption timestamp follows its persisted due instant."
                    )
                );
                checks.Add(
                    new(
                        "timer-due-kept",
                        input.Key,
                        armed?.DueAtUtc is not null && timer?.DueAtUtc == armed.DueAtUtc,
                        "The restart kept the due instant the timer was armed with instead of arming it again."
                    )
                );
                checks.Add(
                    new(
                        "timer-consumed",
                        input.Key,
                        timer?.Status == CheckpointStatusCode.Consumed,
                        "The resumed handler consumed the timer."
                    )
                );
            }
            else
            {
                var boundary = input.Boundary!;
                var pid = await effects.WaitBarrierAsync(boundary, TimeSpan.FromSeconds(30), ct);
                worker = workers.FirstOrDefault(w => w.Id == pid) ?? worker;
                var inFlight = await jobs.GetAsync(root, ct);
                var reached = pid == worker.Id && inFlight?.Status == JobStatusCode.Executing;
                if (!reached)
                {
                    return CaseResult.Incomplete(
                        scenario.Id,
                        environment.Provider,
                        "fault",
                        "Required barrier was not observed on a durably in-flight root."
                    );
                }
                if (scenario.Id == "crash-after-checkpoint")
                {
                    var snapshot = await reader.OutcomesAsync(ct);
                    checks.Add(
                        new(
                            "checkpoint-before-kill",
                            input.Key,
                            snapshot.Single(r => r.Key == "root").Steps?.Contains("step-2=14", StringComparer.Ordinal) == true,
                            "Step 2 was independently visible before termination."
                        )
                    );
                }
                if (scenario.Id == "parent-dies-during-fan-out")
                {
                    var snapshot = await reader.ReadAsync(null, ct);
                    checks.Add(
                        new(
                            "partial-creation",
                            input.Key,
                            snapshot.Count(r => r.ParentJobId == root.JobId) == 4,
                            "Exactly four children existed at the injection boundary."
                        )
                    );
                }
                if (scenario.Id == "database-crash")
                {
                    await environment.RestartDatabaseAsync(ct);
                    await effects.ReleaseAsync(ct);
                }
                else
                {
                    if (scenario.Id == "rolling-restart")
                    {
                        await effects.ReleaseAsync(ct);
                        checks.Add(
                            new(
                                "graceful-worker-stop",
                                input.Key,
                                await worker.StopAsync(ct),
                                "Rolling restart acknowledged graceful process shutdown while another worker remained available."
                            )
                        );
                    }
                    else if (scenario.Id == "kill-all-workers")
                    {
                        foreach (var process in workers)
                        {
                            if (!process.Kill())
                            {
                                return CaseResult.Incomplete(
                                    scenario.Id,
                                    environment.Provider,
                                    "fault",
                                    "Owned worker termination was not acknowledged."
                                );
                            }
                        }
                    }
                    else
                    {
                        if (!worker.Kill())
                        {
                            return CaseResult.Incomplete(
                                scenario.Id,
                                environment.Provider,
                                "fault",
                                "Owned worker termination was not acknowledged."
                            );
                        }
                    }
                    if (scenario.Id == "cancel-descendants")
                    {
                        await jobs.CancelAsync(root, ct: ct);
                    }
                    if (scenario.Id != "kill-busy-worker")
                    {
                        for (var i = 0; i < (scenario.Id == "kill-all-workers" ? 2 : 1); i++)
                        {
                            var replacement = OwnedWorker.Start(state, effects);
                            workers.Add(replacement);
                            await replacement.ReadyAsync(ct);
                        }
                    }
                }
                fault = new(boundary, [input.Key], reached, true, false, RecordedAtUtc: DateTimeOffset.UtcNow);
                observingRecovery = true;
                await WaitAsync(
                    async () => (await RecoveryReadAsync(() => jobs.GetAsync(root, ct).AsTask()))?.Status.IsTerminal == true,
                    bound,
                    ct
                );
                var finalRoot = await RecoveryReadAsync(() => jobs.GetAsync(root, ct).AsTask());
                var reclaim = finalRoot is { ExecutionNumber: > 1 };
                fault = fault with { ReclaimObserved = reclaim };
                if (scenario.Id != "cancel-descendants")
                {
                    checks.Add(
                        new(
                            "observed-reclaim",
                            input.Key,
                            reclaim || scenario.Id is "database-crash" or "rolling-restart",
                            "Recovered attempt observed in durable execution number."
                        )
                    );
                }
            }
            if (sideInputs.Length > 0)
            {
                await plan.DrainAsync(async () => (await RecoveryReadAsync(() => reader.UnfinishedRootsAsync(0, ct))).Count, bound, ct);
            }
            if (scenario.Id == "crash-after-checkpoint")
            {
                var events = await host
                    .Services.GetRequiredService<IActaOperations>()
                    .Ledger.ListEventsAsync(
                        new(JobNamespace: "verify", JobId: root.JobId, EventCode: EventCode.JobNoteRecorded, PageSize: 100),
                        ct
                    );
                foreach (var step in Enumerable.Range(1, input.Steps))
                {
                    checks.Add(
                        new(
                            "checkpoint-body-once",
                            $"step-{step}",
                            events.Items.Count(e => e.Message == $"checkpoint-body {step}") == 1,
                            "Committed checkpoint bodies are reused after recovery, independently witnessed by durable notes."
                        )
                    );
                }
            }
            var actual = await RecoveryReadAsync(() => reader.OutcomesAsync(ct));
            if (scenario.Id == "cancel-descendants")
            {
                checks.AddRange(OutcomeCheck.Reconcile(expected, actual));
                checks.Add(
                    new(
                        "cancelled-parent",
                        input.Key,
                        actual.Single(r => r.Key == "root").Status == "Cancelled",
                        "Parent cancellation is durable."
                    )
                );
                checks.Add(
                    new(
                        "descendant-convergence",
                        input.Key,
                        actual.Where(r => r.Parent is not null).All(r => r.Status is "Cancelled" or "Succeeded"),
                        "Already finished descendants remain finished; unfinished descendants converge."
                    )
                );
            }
            else
            {
                checks.AddRange(OutcomeCheck.Reconcile(expected, actual));
            }
            var sink = await effects.CountsAsync(ct);
            if (input.EffectMode is not null)
            {
                checks.Add(
                    new(
                        "external-effect-applications",
                        input.Key,
                        input.AtMostOnce || input.Boundary == "completion" ? sink.Requests == 1 && sink.Applications == 1
                            : input.EffectMode == "idempotent" ? sink.Requests >= 2 && sink.Applications == 1
                            : sink.Requests >= 2 && sink.Applications == sink.Requests,
                        $"Independent sink: {sink.Requests} requests, {sink.Applications} applications. Idempotency belongs to the sink."
                    )
                );
            }
            bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
            fault = fault! with
            {
                Provider = environment.Provider,
                CaseId = scenario.Id,
                Repetition = repetition,
                RecordedAtUtc = fault.RecordedAtUtc ?? DateTimeOffset.UtcNow,
            };
            bundle.Write($"histories/{tag}-fault.json", fault, VerifyJson.Default.FaultEvidence);
            bundle.Append("faults.jsonl", fault!, VerifyJson.Default.FaultEvidence);
            var covered = OutcomeCheck.FaultCovered(fault!, requireReclaim: RequiresReclaim(scenario.Id));
            var passed = checks.Where(c => c.Code != "observed-reclaim").All(c => c.Passed);
            return new(
                scenario.Id,
                environment.Provider,
                "fault",
                repetition,
                !passed ? "FAIL"
                    : covered ? "PASS"
                    : "INCONCLUSIVE",
                !passed ? "Fault outcome check failed; exact evidence retained."
                    : covered
                        ? scenario.Id == "database-crash"
                                ? "Controller probes retry transient post-crash reads; the worker process is not restarted by the harness."
                            : null
                    : "Required injection/reclaim condition was not established.",
                passed ? "PASS" : "FAIL",
                passed ? "PASS" : "UNVERIFIED",
                covered ? "COMPLETE" : "INCOMPLETE",
                "NOT_MEASURED",
                new()
                {
                    ["external_requests"] = new(sink.Requests, "requests"),
                    ["external_applications"] = new(sink.Applications, "applications"),
                    ["controller_recovery_read_retries"] = new(transientReadRetries, "retries"),
                },
                [
                    $"histories/{tag}-checks.json",
                    $"histories/{tag}-fault.json",
                    .. bundle.WriteRows(tag, passed && covered, expected, actual),
                ],
                Instrumented: true
            );
        }
        catch (TimeoutException) when (observingRecovery && fault is { BoundaryObserved: true, InjectionAcknowledged: true })
        {
            var observer = new DurableReader(state);
            ObservedOutcome[] actual;
            try
            {
                actual = await observer.OutcomesAsync(ct);
            }
            catch (DbException)
            {
                return CaseResult.Incomplete(
                    scenario.Id,
                    environment.Provider,
                    "fault",
                    "The injected fault was acknowledged, but the controller could not reconcile durable state within the recovery bound."
                );
            }
            var reconciliation = OutcomeCheck.Reconcile(expected, actual);
            var unresolved = actual
                .Where(o => o.Status is not ("Succeeded" or "Failed" or "Cancelled"))
                .Select(o => o.Key.Split('/')[0])
                .ToHashSet(StringComparer.Ordinal);
            var acknowledged = expected.Where(o => o.Parent is null).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
            var safety =
                OutcomeCheck.SafetyFailures(reconciliation, unresolved, acknowledged.Contains).Length > 0 ? "FAIL"
                : unresolved.Count > 0 ? "UNVERIFIED"
                : "PASS";
            checks.AddRange(reconciliation);
            checks.Add(
                new(
                    "recovery-liveness",
                    input.Key,
                    false,
                    $"Acknowledged work did not reach its required observed outcome within {bound} seconds after the recovery observation began."
                )
            );
            fault = fault with { Provider = environment.Provider, CaseId = scenario.Id, Repetition = repetition };
            bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
            var rows = bundle.WriteRows(tag, false, expected, actual);
            bundle.Write($"histories/{tag}-fault.json", fault, VerifyJson.Default.FaultEvidence);
            bundle.Append("faults.jsonl", fault, VerifyJson.Default.FaultEvidence);
            return new(
                scenario.Id,
                environment.Provider,
                "fault",
                repetition,
                "FAIL",
                "Observed recovery exceeded its declared liveness bound; exact unresolved identities are retained.",
                safety,
                "FAIL",
                "INCOMPLETE",
                "NOT_MEASURED",
                new() { ["recovery_bound"] = new(bound, "seconds") },
                [$"histories/{tag}-checks.json", $"histories/{tag}-fault.json", .. rows],
                Instrumented: true
            );
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }
        }

        async Task<T> RecoveryReadAsync<T>(Func<Task<T>> query)
        {
            var deadline = Stopwatch.GetTimestamp() + bound * Stopwatch.Frequency;
            while (true)
            {
                try
                {
                    return await query();
                }
                catch (DbException ex) when (scenario.Id == "database-crash" && ex.IsTransient && Stopwatch.GetTimestamp() < deadline)
                {
                    transientReadRetries++;
                    await Task.Delay(100, ct);
                }
            }
        }
    }

    /// <summary>
    /// Waits for <paramref name="condition"/> with a fixed bound; a wait on a set of jobs completing is
    /// <see cref="RunPlan.DrainAsync"/>.
    /// </summary>
    internal static async Task WaitAsync(Func<Task<bool>> condition, int seconds, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                throw new TimeoutException("Declared recovery bound elapsed.");
            }
            await Task.Delay(25, ct);
        }
    }
}
