using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class ConnectivityCase
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
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        await using var proxy = new CutProxy(state.Provider, state.ConnectionString);
        using var worker = OwnedWorker.Start(state, connectionOverride: proxy.ConnectionString);
        await worker.ReadyAsync(ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var input = Enumerable.Range(0, 20).Select(i => new WorkInput($"root-{i}", WorkMs: i == 0 ? 2000 : 10)).ToArray();
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest("verify", "work", JobPayload.Json(input[0], WorkJson.Default.WorkInput), DeduplicationKey: input[0].Key),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Executing,
            plan.Deadline(input.Length, worker.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        proxy.Cut();
        for (var i = 1; i < input.Length; i++)
        {
            await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    "verify",
                    "work",
                    JobPayload.Json(input[i], WorkJson.Default.WorkInput),
                    DeduplicationKey: input[i].Key
                ),
                ct
            );
            await Task.Delay(100, ct);
        }
        proxy.Restore();
        var reader = new DurableReader(state);
        await plan.DrainAsync(
            async () => (await reader.UnfinishedRootsAsync(0, ct)).Count,
            plan.Deadline(input.Length, worker.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        var actual = await reader.OutcomesAsync(ct);
        var checks = OutcomeCheck.Reconcile(WorkloadModel.Expected(input), actual).ToList();
        checks.Add(
            new(
                "live-connection-cut",
                input[0].Key,
                proxy.DroppedConnections > 0,
                "Owned worker connections were closed while a durably executing root was in flight."
            )
        );
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var covered = proxy.DroppedConnections > 0;
        var passed = checks.Where(c => c.Code != "live-connection-cut").All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "fault",
            repetition,
            !passed ? "FAIL"
                : covered ? "PASS"
                : "INCONCLUSIVE",
            "Black-box connectivity failure; no specific lost-transaction-reply boundary is inferred.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            covered ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed && covered, WorkloadModel.Expected(input), actual)]
        );
    }
}
