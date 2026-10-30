using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class RegistrationCases
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
        var tag = $"{environment.Provider}-{scenario.Id}-contract-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var input = new WorkInput("root");
        ExpectedOutcome[] expected =
        [
            new(
                "root",
                scenario.Id == "incompatible-contract" ? "Suspended" : "Succeeded",
                scenario.Id == "incompatible-contract" ? null : "7",
                Steps: ["value={\"value\":7}"]
            ),
        ];
        using var first = OwnedWorker.Start(state, fixture: "v1");
        await first.ReadyAsync(ct);
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                "verify",
                "evolution-fixture",
                JobPayload.Json(input, WorkJson.Default.WorkInput),
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
        using var second = OwnedWorker.Start(state, fixture: scenario.Id == "incompatible-contract" ? "incompatible" : "v2");
        var rejected = false;
        if (scenario.Id == "incompatible-contract")
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

        var checks = OutcomeCheck.Reconcile(expected, actual).ToList();
        if (scenario.Id == "incompatible-contract")
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
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            repetition,
            passed ? "PASS" : "FAIL",
            "Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades.",
            passed ? "PASS" : "FAIL",
            scenario.Id == "incompatible-contract" ? "NOT_APPLICABLE"
                : passed ? "PASS"
                : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed, expected, actual)]
        );
    }
}
