using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class TimeoutAndWakeupCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct,
        int repetition = 1
    )
    {
        var tag = $"{environment.Provider}-{scenario.Id}-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var oracle = new EffectService(bundle.Resolve($"repro/state/{tag}/oracle.db"));
        await oracle.StartAsync(ct);
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        using var worker = OwnedWorker.Start(state, oracle, suppressWakeup: scenario.Id == "F08");
        await worker.ReadyAsync(ct);
        var input = new BenchWork("root", Children: scenario.Id == "F08" ? 10 : 0);
        var expected = WorkloadModel.Expected([input]);
        if (scenario.Id == "R03")
        {
            expected[0] = expected[0] with { Status = "Failed", Output = null };
        }
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                "bench",
                scenario.Id == "R03" ? "late-work" : "work",
                JobPayload.Json(input, WorkJson.Default.BenchWork),
                DeduplicationKey: input.Key
            ),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status.IsTerminal == true,
            plan.Deadline(1 + input.Children, 0, state.DrainRate),
            ct
        );
        if (scenario.Id == "R03")
        {
            await Task.Delay(3000, ct);
        }
        var actual = await new DurableReader(state).OutcomesAsync(ct);
        var checks = OutcomeOracle.Reconcile(expected, actual).ToList();
        if (scenario.Id == "R03")
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
            var effects = await oracle.CountsAsync(ct);
            checks.Add(
                new(
                    "late-external-effect",
                    input.Key,
                    effects.Requests == 1 && effects.Applications == 1,
                    "External operation survived the engine timeout; no exactly-once cancellation claim."
                )
            );
        }
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        var covered = scenario.Id != "F08" || worker.DroppedWakeups > 0;
        return new(
            scenario.Id,
            environment.Provider,
            "fault",
            repetition,
            !passed ? "FAIL"
                : covered ? "PASS"
                : "INCONCLUSIVE",
            !covered ? "The worker dropped no wake signal, so the fallback had nothing to cover."
                : scenario.Id == "F08"
                    ? "In-process wakeup accelerator disabled; durable safety polling completed the same native child/join workload."
                : null,
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            covered ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            scenario.Id == "F08" ? new() { ["dropped_wakeups"] = new(worker.DroppedWakeups, "signals") } : [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed, expected, actual)],
            Instrumented: true
        );
    }
}
