using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class OutboxCase
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
        var source = new SqliteConnectionStringBuilder { DataSource = bundle.Resolve($"repro/state/{tag}/producer.db") }.ToString();
        var input = Enumerable.Range(0, 12).Select(i => new WorkInput($"root-{i}")).ToArray();
        var keyless = scenario.Id == "outbox-keyless-redelivery";
        List<JobRef> refs = [];
        await using (var connection = new SqliteConnection(source))
        {
            await connection.OpenAsync(ct);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE domain_obligations(business_key TEXT PRIMARY KEY);"
                    + SqliteMigrations.CreateOutboxScript();
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
                refs.Add(
                    await transaction.AddToActaOutboxAsync(
                        new EnqueueJobRequest(
                            "verify",
                            "work",
                            JobPayload.Json(work, WorkJson.Default.WorkInput),
                            IdempotencyKey: keyless ? null : work.Key
                        ),
                        cancellationToken: ct
                    )
                );
            }
            await transaction.CommitAsync(ct);
        }
        using var effects = new EffectService(bundle.Resolve($"repro/state/{tag}/effects.db"));
        await effects.StartAsync(ct);
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        using var original = OwnedWorker.Start(state, effects, new("EnqueueJobs", "outbox-enqueued"), outboxSource: source);
        await original.ReadyAsync(ct);
        var pid = await effects.WaitBarrierAsync("outbox-enqueued", TimeSpan.FromSeconds(60), ct);
        if (pid != original.Id)
        {
            return CaseResult.Incomplete(
                scenario.Id,
                environment.Provider,
                "fault",
                "Relay enqueue/acknowledgement boundary was not reached."
            );
        }
        var reader = new DurableReader(state);
        var jobs = host.Services.GetRequiredService<IJobs>();
        // Each job is named by the record whose staged ref finds it, keyed or not. A job no staged ref finds keeps the
        // reader's name, so a second job for one record is an unexpected identity.
        Dictionary<long, string> keys = [];
        foreach (var (work, jobRef) in input.Zip(refs))
        {
            if (await jobs.GetIdAsync(jobRef, ct) is { } job)
            {
                keys[job.JobId] = work.Key;
            }
        }
        var before = (await reader.ReadAsync(null, ct)).Select(Named).ToList();
        var stagedAtKill = await StagedAsync();
        if (!original.Kill())
        {
            return CaseResult.Incomplete(scenario.Id, environment.Provider, "fault", "Owned relay termination was not acknowledged.");
        }
        using var replacement = OwnedWorker.Start(state, outboxSource: source);
        await replacement.ReadyAsync(ct);
        await plan.DrainAsync(
            async () => input.Length - (await reader.ReadAsync(null, ct)).Count(r => r.Status == 100),
            plan.Deadline(input.Length, original.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => await StagedAsync() == 0,
            plan.Deadline(input.Length, original.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        var actual = DurableReader.Outcomes([.. (await reader.ReadAsync(null, ct)).Select(Named)], await reader.StepsAsync(null, ct));
        var checks = OutcomeCheck.Reconcile(WorkloadModel.Expected(input), actual).ToList();
        checks.Add(
            new(
                "staged-at-relay-kill",
                "outbox",
                stagedAtKill == input.Length,
                $"{stagedAtKill} of {input.Length} records were still staged when the relay was killed, so the new relay delivered each again."
            )
        );
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
            scenario.Id,
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

        DurableRow Named(DurableRow row) => keys.TryGetValue(row.JobId, out var key) ? row with { Key = key } : row;

        async Task<long> StagedAsync()
        {
            await using var connection = new SqliteConnection(source);
            await connection.OpenAsync(ct);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM acta_outbox";
            return (long)(await count.ExecuteScalarAsync(ct))!;
        }
    }
}
