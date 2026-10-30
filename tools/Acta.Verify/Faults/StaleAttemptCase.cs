using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class StaleAttemptCase
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
        using var original = OwnedWorker.Start(state, effects);
        await original.ReadyAsync(ct);
        var input = new WorkInput("root", Boundary: "claim");
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest("verify", "work", JobPayload.Json(input, WorkJson.Default.WorkInput), DeduplicationKey: input.Key),
            ct
        );
        var pid = await effects.WaitBarrierAsync("claim", TimeSpan.FromSeconds(30), ct);
        if (pid != original.Id)
        {
            return CaseResult.Incomplete(scenario.Id, environment.Provider, "fault", "Busy worker boundary was not observed.");
        }
        original.Pause();
        try
        {
            using var replacement = OwnedWorker.Start(state, effects);
            await replacement.ReadyAsync(ct);
            await FaultCaseRunner.WaitAsync(
                async () => (await jobs.GetAsync(root, ct)) is { Status: JobStatusCode.Succeeded, ExecutionNumber: > 1 },
                plan.Deadline(1, original.Settings!.LeaseSeconds, state.DrainRate),
                ct
            );
            await effects.ReleaseAsync(ct);
        }
        finally
        {
            original.Resume();
        }
        if (await effects.WaitBarrierAsync("claim-returned", TimeSpan.FromSeconds(30), ct) != original.Id)
        {
            return CaseResult.Incomplete(
                scenario.Id,
                environment.Provider,
                "fault",
                "The stale attempt never returned a result to complete."
            );
        }
        // Permit the engine to attempt the stale attempt's guarded completion.
        await Task.Delay(3000, ct);
        var actual = await new DurableReader(state).OutcomesAsync(ct);
        var checks = OutcomeCheck.Reconcile(WorkloadModel.Expected([input]), actual).ToList();
        await using var connection = await state.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var table = state.Provider == "sqlite" ? "main.jobs_state" : $"{state.Schema}.jobs_state";
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE job_id=@id AND execution_number=1 AND result_format_id <> 0";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = root.JobId;
        command.Parameters.Add(parameter);
        checks.Add(
            new(
                "stale-result-rejected",
                input.Key,
                Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0,
                "Old physical execution did not acquire an authoritative result after reclaim."
            )
        );
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "fault",
            repetition,
            passed ? "PASS" : "FAIL",
            passed ? null : "Stale authoritative settlement check failed.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed, WorkloadModel.Expected([input]), actual)],
            Instrumented: true
        );
    }
}
