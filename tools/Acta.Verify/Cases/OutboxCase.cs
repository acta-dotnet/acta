using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class OutboxCase
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct,
        int repetition = 1
    )
    {
        var tag = $"{environment.Provider}-I03-outbox-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        var source = new SqliteConnectionStringBuilder { DataSource = bundle.Resolve($"repro/state/{tag}/producer.db") }.ToString();
        var input = Enumerable.Range(0, 12).Select(i => new BenchWork($"root-{i}")).ToArray();
        await using (var connection = new SqliteConnection(source))
        {
            await connection.OpenAsync(ct);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE domain_obligations(business_key TEXT PRIMARY KEY);"
                    + SqliteOutboxDdl.CreateScript();
                await create.ExecuteNonQueryAsync(ct);
            }
            await using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var work in input)
            {
                await using var domain = connection.CreateCommand();
                domain.Transaction = transaction;
                domain.CommandText = "INSERT INTO domain_obligations VALUES(@key)";
                domain.Parameters.AddWithValue("@key", work.Key);
                await domain.ExecuteNonQueryAsync(ct);
                await transaction.AddToActaOutboxAsync(
                    new EnqueueJobRequest("bench", "work", JobPayload.Json(work, WorkJson.Default.BenchWork), DeduplicationKey: work.Key),
                    cancellationToken: ct
                );
            }
            await transaction.CommitAsync(ct);
        }
        using var oracle = new EffectService(bundle.Resolve($"repro/state/{tag}/oracle.db"));
        await oracle.StartAsync(ct);
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        using var original = OwnedWorker.Start(state, oracle, new("EnqueueJobs", "outbox-enqueued"), outboxSource: source);
        await original.ReadyAsync(ct);
        var pid = await oracle.WaitBarrierAsync("outbox-enqueued", TimeSpan.FromSeconds(60), ct);
        if (pid != original.Id)
        {
            return CaseResult.Incomplete("I03", environment.Provider, "fault", "Relay enqueue/acknowledgement boundary was not reached.");
        }
        var reader = new DurableReader(state);
        var before = await reader.ReadAsync(null, ct);
        if (!original.Kill())
        {
            return CaseResult.Incomplete("I03", environment.Provider, "fault", "Owned relay termination was not acknowledged.");
        }
        using var replacement = OwnedWorker.Start(state, outboxSource: source);
        await replacement.ReadyAsync(ct);
        await plan.DrainAsync(
            async () => input.Length - (await reader.ReadAsync(null, ct)).Count(r => r.Status == 100),
            plan.Deadline(input.Length, original.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () =>
            {
                await using var connection = new SqliteConnection(source);
                await connection.OpenAsync(ct);
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM acta_outbox";
                return (long)(await count.ExecuteScalarAsync(ct))! == 0;
            },
            plan.Deadline(input.Length, original.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        var actual = await reader.OutcomesAsync(ct);
        var checks = OutcomeOracle.Reconcile(WorkloadModel.Expected(input), actual).ToList();
        checks.Add(
            new(
                "committed-before-relay-kill",
                "outbox",
                before
                    .Select(r => r.Key)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(input.Select(i => i.Key).Order(StringComparer.Ordinal)),
                "All logical jobs were visible after destination commit and before the relay could acknowledge its source rows."
            )
        );
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        return new(
            "I03",
            environment.Provider,
            "fault",
            repetition,
            passed ? "PASS" : "FAIL",
            passed ? null : "Outbox recovery check failed.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", .. bundle.WriteRows(tag, passed, WorkloadModel.Expected(input), actual)],
            Instrumented: true
        );
    }
}
