using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class RaceCases
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
        List<Check> checks = [];
        var covered = true;
        List<string> evidence = [];
        foreach (var ordering in new[] { "before", "after" })
        {
            await RunOrderingAsync(ordering);
        }
        var path = $"histories/{environment.Provider}-{scenario.Id}-race-{repetition}-checks.json";
        bundle.Write(path, checks.ToArray(), VerifyJson.Default.CheckArray);
        evidence.Add(path);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            repetition,
            !covered ? "INCONCLUSIVE"
                : passed ? "PASS"
                : "FAIL",
            !covered ? "Required controlled boundary was not reached."
                : passed ? null
                : "Controlled ordering or outcome check failed.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            covered ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            [],
            [.. evidence],
            Instrumented: true
        );

        async Task RunOrderingAsync(string ordering)
        {
            var tag = $"{environment.Provider}-{scenario.Id}-{ordering}-{repetition}";
            var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
            using var effects = new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db"));
            await effects.StartAsync(ct);
            using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
            var jobs = host.Services.GetRequiredService<IJobs>();
            var input =
                scenario.Id == "P05"
                    ? new WorkInput("root", Children: 10, Boundary: "join-" + ordering)
                    : new WorkInput("root", Signal: "go", SignalTimeoutSeconds: scenario.Id == "T05" ? 5 : 0);
            var expected = WorkloadModel.Expected([input]);
            if (scenario.Id == "T05")
            {
                expected[0] = expected[0] with { Output = ordering == "before" ? "signalled" : "expired" };
            }
            var root = await jobs.EnqueueAsync(
                new EnqueueJobRequest("verify", "work", JobPayload.Json(input, WorkJson.Default.WorkInput), DeduplicationKey: input.Key),
                ct
            );
            using var worker = OwnedWorker.Start(state, effects, scenario.Id == "T03" ? new("WaitSignal", "wait-registered") : null);
            await worker.ReadyAsync(ct);
            var reader = new DurableReader(state);
            if (scenario.Id == "P05")
            {
                var boundary = ordering == "before" ? "join-before" : "claim";
                var reached = await effects.WaitBarrierAsync(boundary, TimeSpan.FromSeconds(30), ct) is not null;
                if (!reached)
                {
                    covered = false;
                    return;
                }
                checks.Add(new("join-boundary-hit", ordering, reached, "Observed held parent or final-child boundary."));
                if (ordering == "before")
                {
                    await plan.DrainAsync(
                        async () => 10 - (await reader.ReadAsync(null, ct)).Count(r => r.ParentJobId == root.JobId && r.Status == 100),
                        plan.Deadline(expected.Length, 0, state.DrainRate),
                        ct
                    );
                }
                else
                {
                    await FaultCaseRunner.WaitAsync(
                        async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
                        plan.Deadline(expected.Length, 0, state.DrainRate),
                        ct
                    );
                }
                await effects.ReleaseAsync(ct);
            }
            else if (scenario.Id == "T03")
            {
                var hit = await effects.WaitBarrierAsync("wait-registered", TimeSpan.FromSeconds(30), ct) is not null;
                var checkpoints = await jobs.GetCheckpointsAsync(root, ct);
                checks.Add(
                    new(
                        "wait-registration-hit",
                        ordering,
                        hit && checkpoints?.Items.Any(c => c.Name == "go") == true,
                        "Wait registration committed while its result was held before returning to the handler."
                    )
                );
                if (!hit)
                {
                    covered = false;
                    return;
                }
                if (ordering == "after")
                {
                    await effects.ReleaseAsync(ct);
                    await FaultCaseRunner.WaitAsync(
                        async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
                        plan.Deadline(expected.Length, 0, state.DrainRate),
                        ct
                    );
                    await jobs.RaiseSignalAsync(root, "go", default(JobPayload), ct: ct);
                }
                else
                {
                    await jobs.RaiseSignalAsync(root, "go", default(JobPayload), ct: ct);
                    await effects.ReleaseAsync(ct);
                }
            }
            else
            {
                await FaultCaseRunner.WaitAsync(
                    async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
                    plan.Deadline(expected.Length, 0, state.DrainRate),
                    ct
                );
                if (ordering == "after")
                {
                    await FaultCaseRunner.WaitAsync(
                        async () => (await jobs.GetAsync(root, ct))?.Status.IsTerminal == true,
                        plan.Deadline(expected.Length, 0, state.DrainRate),
                        ct
                    );
                }
                await jobs.RaiseSignalAsync(root, "go", default(JobPayload), ct: ct);
            }
            await FaultCaseRunner.WaitAsync(
                async () => (await jobs.GetAsync(root, ct))?.Status.IsTerminal == true,
                plan.Deadline(expected.Length, 0, state.DrainRate),
                ct
            );
            var actual = await reader.OutcomesAsync(ct);
            checks.AddRange(OutcomeCheck.Reconcile(expected, actual));
            evidence.AddRange(bundle.WriteRows(tag, checks.All(c => c.Passed), expected, actual));
        }
    }
}
