using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

internal static class TransactionCases
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
        var tag = $"{environment.Provider}-{scenario.Id}-transaction-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var table = state.Provider == "sqlite" ? "main.bench_domain" : $"{state.Schema}.bench_domain";
        await using (var connection = await state.OpenAsync(ct))
        {
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {table}(business_key varchar(80) NOT NULL PRIMARY KEY)";
                await create.ExecuteNonQueryAsync(ct);
            }
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {table}(business_key) VALUES('root')";
            await insert.ExecuteNonQueryAsync(ct);
            var input = new BenchWork("root");
            await jobs.EnqueueAsync(
                transaction,
                new EnqueueJobRequest("bench", "work", JobPayload.Json(input, WorkJson.Default.BenchWork), DeduplicationKey: input.Key),
                ct
            );
            if (scenario.Id == "I01")
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }
        }
        using var worker = OwnedWorker.Start(state);
        await worker.ReadyAsync(ct);
        var reader = new DurableReader(state);
        if (scenario.Id == "I01")
        {
            await FaultCaseRunner.WaitAsync(
                async () => (await jobs.GetAsync(JobLookup.ByDeduplicationKey("bench", "root"), ct))?.Status == JobStatusCode.Succeeded,
                plan.Deadline(1, 0, state.DrainRate),
                ct
            );
        }
        var actual = await reader.OutcomesAsync(ct);
        var checks = OutcomeOracle.Reconcile(scenario.Id == "I01" ? WorkloadModel.Expected([new("root")]) : [], actual).ToList();
        await using (var connection = await state.OpenAsync(ct))
        {
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT business_key FROM {table} ORDER BY business_key";
            List<string> obligations = [];
            await using var obligationReader = await read.ExecuteReaderAsync(ct);
            while (await obligationReader.ReadAsync(ct))
            {
                obligations.Add(obligationReader.GetString(0));
            }
            checks.Add(
                new(
                    "domain-transaction",
                    "root",
                    scenario.Id == "I01" ? obligations.Count == 1 && obligations[0] == "root" : obligations.Count == 0,
                    "Independently reconnected domain table agrees with the committed/rolled-back obligation model."
                )
            );
        }
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
        bundle.Write($"histories/{tag}-observed.json", actual, BenchJson.Default.ObservedOutcomeArray);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            state.Provider,
            "verify",
            repetition,
            passed ? "PASS" : "FAIL",
            passed ? null : "Transactional obligation mismatch.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", $"histories/{tag}-observed.json"]
        );
    }
}
