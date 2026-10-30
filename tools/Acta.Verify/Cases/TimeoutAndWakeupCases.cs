using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class TimeoutAndWakeupCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        Scenario scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct,
        int repetition = 1
    )
    {
        var tag = $"{environment.Provider}-{scenario.Id}-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var effects = new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db"));
        await effects.StartAsync(ct);
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        using var worker = OwnedWorker.Start(state, effects, suppressWakeup: scenario.Id == "wakeup-transport-down");
        await worker.ReadyAsync(ct);
        var input = new WorkInput("root", Children: scenario.Id == "wakeup-transport-down" ? 10 : 0);
        var expected = WorkloadModel.Expected([input]);
        if (scenario.Id == "timeout-late-attempt")
        {
            expected[0] = expected[0] with { Status = "Failed", Output = null };
        }
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                "verify",
                scenario.Id == "timeout-late-attempt" ? "late-work" : "work",
                JobPayload.Json(input, WorkJson.Default.WorkInput),
                DeduplicationKey: input.Key
            ),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status.IsTerminal == true,
            plan.Deadline(1 + input.Children, 0, state.DrainRate),
            ct
        );
        if (scenario.Id == "timeout-late-attempt")
        {
            await Task.Delay(3000, ct);
        }
        var actual = await new DurableReader(state).OutcomesAsync(ct);
        var checks = OutcomeCheck.Reconcile(expected, actual).ToList();
        if (scenario.Id == "timeout-late-attempt")
        {
            var checkpoints = await jobs.GetCheckpointsAsync(root, ct);
            checks.Add(
                new(
                    "late-authoritative-write",
                    input.Key,
                    checkpoints?.Items.All(c => c.Name != "late-write") == true,
                    "Late external operation remained observable but cancelled attempt could not commit a variable."
                )
            );
            var sink = await effects.CountsAsync(ct);
            checks.Add(
                new(
                    "late-external-effect",
                    input.Key,
                    sink.Requests == 1 && sink.Applications == 1,
                    "External operation survived the engine timeout; no exactly-once cancellation claim."
                )
            );
        }
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        var covered = scenario.Id != "wakeup-transport-down" || worker.DroppedWakeups > 0;
        return new(
            scenario.Id,
            environment.Provider,
            "fault",
            repetition,
            !passed ? "FAIL"
                : covered ? "PASS"
                : "INCONCLUSIVE",
            !covered ? "The worker dropped no wake signal, so the fallback had nothing to cover."
                : scenario.Id == "wakeup-transport-down"
                    ? "In-process wakeup accelerator disabled; durable safety polling completed the same native child/join workload."
                : null,
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            covered ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            scenario.Id == "wakeup-transport-down" ? new() { ["dropped_wakeups"] = new(worker.DroppedWakeups, "signals") } : [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed, expected, actual)],
            Instrumented: true
        );
    }
}
