using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

internal static class RegistrationCases
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
        if (scenario.Id == "D04")
        {
            return new(
                scenario.Id,
                environment.Provider,
                "verify",
                0,
                "NOT_APPLICABLE",
                "This pre-1.0 tree publishes one baseline migration per provider and no supported predecessor upgrade pair.",
                "NOT_APPLICABLE",
                "NOT_APPLICABLE",
                "NOT_APPLICABLE",
                "NOT_MEASURED",
                [],
                ["resolved-plan.json"]
            );
        }
        var tag = $"{environment.Provider}-{scenario.Id}-contract-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var input = new BenchWork("root");
        ExpectedOutcome[] expected =
        [
            new(
                "root",
                scenario.Id == "D03" ? "Suspended" : "Succeeded",
                scenario.Id == "D03" ? null : "7",
                Steps: ["value={\"value\":7}"]
            ),
        ];
        bundle.Write($"histories/{tag}-expected.json", expected, BenchJson.Default.ExpectedOutcomeArray);
        using var first = OwnedWorker.Start(state, fixture: "v1");
        await first.ReadyAsync(ct);
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                "bench",
                "evolution-fixture",
                JobPayload.Json(input, WorkJson.Default.BenchWork),
                DeduplicationKey: input.Key
            ),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
            plan.Deadline(1, 0, state.DrainRate),
            ct
        );
        first.Kill();
        using var second = OwnedWorker.Start(state, fixture: scenario.Id == "D03" ? "incompatible" : "v2");
        var rejected = false;
        if (scenario.Id == "D03")
        {
            try
            {
                await second.ReadyAsync(ct);
            }
            catch (InvalidOperationException)
            {
                rejected = await second.ContractRejectedAsync(ct);
            }
        }
        else
        {
            await second.ReadyAsync(ct);
            await jobs.RaiseSignalAsync(root, "continue", default(JobPayload), ct: ct);
            await FaultCaseRunner.WaitAsync(
                async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Succeeded,
                plan.Deadline(1, 0, state.DrainRate),
                ct
            );
        }
        var actual = await new DurableReader(state).OutcomesAsync(ct);

        var checks = OutcomeOracle.Reconcile(expected, actual).ToList();
        if (scenario.Id == "D03")
        {
            checks.Add(
                new(
                    "startup-contract-guard",
                    "root",
                    rejected,
                    "Worker process reported PayloadContractDriftException under the engine's Fail guard."
                )
            );
        }
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
        bundle.Write($"histories/{tag}-observed.json", actual, BenchJson.Default.ObservedOutcomeArray);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            repetition,
            passed ? "PASS" : "FAIL",
            "Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades.",
            passed ? "PASS" : "FAIL",
            scenario.Id == "D03" ? "NOT_APPLICABLE"
                : passed ? "PASS"
                : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json"]
        );
    }
}
