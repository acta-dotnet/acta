using System.Collections;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class StorageCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        Scenario scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct
    )
    {
        if (scenario.Id is "retention-during-load" or "retention-live-dependencies")
        {
            return await RetentionAsync(plan, scenario, environment, bundle, ct);
        }
        var populations = scenario.SettingList("populations", environment.Provider);
        List<Check> checks = [];
        List<string> evidence = [];
        foreach (var population in populations)
        {
            await PopulationAsync(population);
        }
        var path = $"histories/{environment.Provider}-{scenario.Id}-storage-checks.json";
        bundle.Write(path, checks.ToArray(), VerifyJson.Default.CheckArray);
        evidence.Add(path);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            0,
            passed ? "PASS" : "FAIL",
            passed ? null : "Retained population or due-root outcome check failed.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [.. evidence]
        );

        async Task PopulationAsync(int population)
        {
            var tag = $"{environment.Provider}-{scenario.Id}-{population}";
            var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
            using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
            var jobs = host.Services.GetRequiredService<IJobs>();
            using var worker = OwnedWorker.Start(state);
            await worker.ReadyAsync(ct);
            await using var resources = new ResourceSampler(state, bundle, tag, [worker.Id], ct);
            bundle.Write(
                $"histories/{tag}-population-model.json",
                new Dictionary<string, string>
                {
                    ["keys"] = $"history-0 through history-{population - 1}; each identity exactly once",
                    ["expected_status"] = DurableReader.Status(BackgroundStatus(scenario, environment.Provider)),
                    ["result_model"] =
                        BackgroundStatus(scenario, environment.Provider) == 100
                            ? "SHA256(UTF8(logical key)); no input transformation"
                            : "no result",
                },
                VerifyJson.Default.DictionaryStringString
            );
            await PrepareBackgroundAsync(plan, scenario, state, jobs, resources, population, ct);
            var due = Enumerable.Range(0, 12).Select(i => new WorkInput($"root-{i}")).ToArray();
            var ids = new List<long>();
            foreach (var work in due)
            {
                ids.Add(
                    (
                        await jobs.EnqueueAsync(
                            new EnqueueJobRequest(
                                "verify",
                                "work",
                                JobPayload.Json(work, WorkJson.Default.WorkInput),
                                DeduplicationKey: work.Key
                            ),
                            ct
                        )
                    ).JobId
                );
            }
            var reader = new DurableReader(state);
            await plan.DrainAsync(
                async () => (await reader.ReadAsync(ids, ct)).Count(r => r.Status != 100),
                plan.Deadline(due.Length, 0, state.DrainRate),
                ct
            );
            var rows = await reader.ReadAsync(null, ct);
            var observedDue = rows.Where(r => r.Key.StartsWith("root-", StringComparison.Ordinal))
                .Select(r => new ObservedOutcome(r.Key, DurableReader.Status(r.Status), r.Output, JobNamespace: r.JobNamespace))
                .ToArray();
            Check[] populationChecks =
            [
                .. OutcomeCheck.Reconcile(WorkloadModel.Expected(due), observedDue),
                .. await CheckBackgroundAsync(state, scenario, population, ct),
            ];
            checks.AddRange(populationChecks);
            var failing = populationChecks.Where(c => !c.Passed).Select(c => c.Identity).ToHashSet(StringComparer.Ordinal);
            if (failing.Count > 0)
            {
                bundle.Write(
                    $"histories/{tag}-rows.json",
                    rows.Where(r => failing.Contains(r.Key)).ToArray(),
                    TrialJson.Default.DurableRowArray
                );
                evidence.Add($"histories/{tag}-rows.json");
            }
        }
    }

    internal static async Task<int> PrepareBackgroundAsync(
        RunPlan plan,
        Scenario scenario,
        OwnedProvider state,
        IJobs jobs,
        ResourceSampler? resources,
        int size,
        CancellationToken ct
    )
    {
        var status = BackgroundStatus(scenario, state.Provider);
        var future = DateTime.UtcNow.AddDays(1);
        // The population is seeded before any measurement, so its batches go in concurrently.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, (size + 999) / 1000),
            new ParallelOptions { MaxDegreeOfParallelism = (int)plan.Policy("measurement", "preload_producers"), CancellationToken = ct },
            async (index, token) =>
            {
                if (resources?.BudgetExceeded == true)
                {
                    throw new ResourceBudgetException("Background population exceeded its memory/disk budget.");
                }
                var first = index * 1000;
                var batch = Enumerable
                    .Range(first, Math.Min(1000, size - first))
                    .Select(i =>
                    {
                        var work = new WorkInput($"history-{i}", Signal: status == 20 ? "park" : null);
                        return new EnqueueJobRequest(
                            "verify",
                            "work",
                            JobPayload.Json(work, WorkJson.Default.WorkInput),
                            DeduplicationKey: work.Key,
                            Priority: status == 10 ? JobPriorityCode.High : JobPriorityCode.Normal,
                            NextRunAtUtc: status == 10 ? future : null
                        );
                    })
                    .ToArray();
                await jobs.EnqueueBatchAsync(batch, token);
                if (first % 10000 == 0)
                {
                    Console.WriteLine($"  {state.Provider} {scenario.Id}: background batch {index + 1}/{(size + 999) / 1000}");
                }
            }
        );
        await plan.DrainAsync(
            async () => size - await PopulationCountAsync(state, status, ct),
            plan.Deadline(size, 0, state.DrainRate),
            ct
        );
        return size;
    }

    internal static async Task<Check[]> CheckBackgroundAsync(OwnedProvider state, Scenario scenario, int population, CancellationToken ct)
    {
        var status = BackgroundStatus(scenario, state.Provider);
        var seen = new BitArray(population);
        List<Check> checks = [];
        await foreach (var page in new DurableReader(state).RootPagesAsync(0, ct))
        foreach (var row in page.Where(r => r.Key.StartsWith("history-", StringComparison.Ordinal)))
        {
            if (
                !int.TryParse(row.Key.AsSpan(8), CultureInfo.InvariantCulture, out var ordinal)
                || ordinal < 0
                || ordinal >= population
                || seen[ordinal]
                || row.Status != status
            )
            {
                checks.Add(
                    new("background-identity-state", row.Key, false, "Unexpected, duplicate, resumed, or early-executed background job.")
                );
                continue;
            }
            seen[ordinal] = true;
            if (status == 100 && row.Output != WorkloadModel.Expected([new(row.Key)])[0].Output)
            {
                checks.Add(new("retained-result", row.Key, false, "Retained result differs from independent output."));
            }
        }
        for (var i = 0; i < population; i++)
        {
            if (!seen[i])
            {
                checks.Add(
                    new(
                        "missing-background-identity",
                        $"history-{i}",
                        false,
                        "Required background identity was not retained in its expected state."
                    )
                );
            }
        }
        checks.Add(
            new(
                "background-reconciled",
                scenario.Id,
                checks.Count == 0,
                $"Exact {population}-identity background reconciled independently."
            )
        );
        return [.. checks];
    }

    private static async Task<CaseResult> RetentionAsync(
        RunPlan plan,
        Scenario scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct
    )
    {
        var tag = $"{environment.Provider}-{scenario.Id}-retention";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var operations = host.Services.GetRequiredService<IActaOperations>();
        var definition =
            await operations.Definitions.GetAsync("verify", "work", ct) ?? throw new InvalidOperationException("Missing work definition.");
        await operations.Definitions.UpdateOverridesAsync("verify", "work", definition.Version, new(JobRetentionSeconds: 0), ct: ct);
        using var worker = OwnedWorker.Start(state);
        await worker.ReadyAsync(ct);
        var input = new WorkInput(
            "root",
            Children: scenario.Id == "retention-live-dependencies" ? 10 : 0,
            Signal: scenario.Id == "retention-during-load" ? "hold" : null,
            AfterChildrenSignal: scenario.Id == "retention-live-dependencies" ? "hold" : null
        );
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest("verify", "work", JobPayload.Json(input, WorkJson.Default.WorkInput), DeduplicationKey: input.Key),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
            plan.Deadline(1, 0, state.DrainRate),
            ct
        );
        List<Check> checks = [];
        var maintenance = host.Services.GetRequiredService<MaintenanceOperations>();
        var ns = (await operations.Namespaces.ListAsync(new(), ct)).Items.Single(n => n.JobNamespace == "verify").NamespaceId;
        var traffic = Enumerable.Range(0, 30).Select(i => new WorkInput($"traffic-{i}", WorkMs: 10)).ToArray();
        foreach (var work in traffic)
        {
            await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    "verify",
                    "retained-work",
                    JobPayload.Json(work, WorkJson.Default.WorkInput),
                    DeduplicationKey: work.Key
                ),
                ct
            );
            await maintenance.PurgeExpiredDataAsync(new(ns, RetentionSection.Jobs, DateTime.UtcNow, 1000), ct);
        }
        await Task.Delay(1000, ct);
        await maintenance.PurgeExpiredDataAsync(new(ns, RetentionSection.Jobs, DateTime.UtcNow, 1000), ct);
        var reader = new DurableReader(state);
        await plan.DrainAsync(
            async () =>
                traffic.Length
                - (await reader.ReadAsync(null, ct)).Count(r => r.Key.StartsWith("traffic-", StringComparison.Ordinal) && r.Status == 100),
            plan.Deadline(traffic.Length, 0, state.DrainRate),
            ct
        );
        var before = await reader.ReadAsync(null, ct);
        var trafficActual = (await reader.OutcomesAsync(ct)).Where(r => r.Key.StartsWith("traffic-", StringComparison.Ordinal));
        checks.AddRange(OutcomeCheck.Reconcile(WorkloadModel.Expected(traffic), trafficActual));
        checks.Add(
            new(
                "live-parent-preserved",
                "root",
                before.Any(r => r.Key == "root" && r.Status == 20),
                "A waiting live root survives eligible-state retention."
            )
        );
        if (scenario.Id == "retention-live-dependencies")
        {
            checks.Add(
                new(
                    "live-dependencies-preserved",
                    "root",
                    before.Count(r => r.ParentJobId == root.JobId) == 10,
                    "Completed children required by a live parent survive retention."
                )
            );
        }
        await jobs.RaiseSignalAsync(root, "hold", default(JobPayload), ct: ct);
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Succeeded,
            plan.Deadline(1, 0, state.DrainRate),
            ct
        );
        var result = await jobs.GetResultAsync<string>(root, ct);
        checks.Add(
            new(
                "retained-parent-result",
                "root",
                result == WorkloadModel.Expected([input])[0].Output,
                "Parent resumes with its expected logical output after retention."
            )
        );
        await Task.Delay(1000, ct);
        await FaultCaseRunner.WaitAsync(
            async () =>
            {
                await maintenance.PurgeExpiredDataAsync(new(ns, RetentionSection.Jobs, DateTime.UtcNow, 1000), ct);
                return await jobs.GetAsync(root, ct) is null;
            },
            plan.Deadline(1, 0, state.DrainRate),
            ct
        );
        checks.Add(
            new(
                "eligible-parent-purged",
                "root",
                await jobs.GetAsync(root, ct) is null,
                "Terminal root becomes eligible under the explicit zero-second policy."
            )
        );
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        if (!passed)
        {
            bundle.Write($"histories/{tag}-before.json", before.ToArray(), TrialJson.Default.DurableRowArray);
        }
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            0,
            passed ? "PASS" : "FAIL",
            passed ? null : "Retention contract check failed.",
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-checks.json", .. passed ? Array.Empty<string>() : [$"histories/{tag}-before.json"]]
        );
    }

    /// <summary>
    /// The status a background job settles in, by the scenario's <c>background_state</c>: waiting on a signal, scheduled
    /// for tomorrow at high priority, or, unwritten, succeeded and retained.
    /// </summary>
    private static int BackgroundStatus(Scenario scenario, string provider) =>
        scenario.SettingText("background_state", provider) switch
        {
            null => 100,
            "waiting" => 20,
            "scheduled" => 10,
            var other => throw new InvalidOperationException($"Scenario {scenario.Id} writes an unknown background_state {other}."),
        };

    private static async Task<long> PopulationCountAsync(OwnedProvider state, int status, CancellationToken ct)
    {
        await using var connection = await state.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var prefix = state.Provider == "sqlite" ? "main" : state.Schema;
        command.CommandText =
            $"SELECT COUNT(*) FROM {prefix}.jobs j JOIN {prefix}.jobs_state r ON r.job_id=j.job_id WHERE j.deduplication_key LIKE 'history-%' AND r.job_status_code={status}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
