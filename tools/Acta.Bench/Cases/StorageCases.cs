using System.Collections;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

internal static class StorageCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct
    )
    {
        if (scenario.Id is "S04" or "S05")
        {
            return await RetentionAsync(plan, scenario, environment, bundle, ct);
        }
        int[] populations =
            scenario.Id == "S01"
                ?
                [
                    .. plan
                        .FullPolicy.GetProperty("profiles")
                        .GetProperty(plan.Profile)
                        .GetProperty("retained_history_populations")
                        .EnumerateArray()
                        .Select(p => p.GetInt32()),
                ]
                : [BackgroundRoots(plan, scenario.Id)];
        List<Check> checks = [];
        List<string> evidence = [];
        foreach (var population in populations)
        {
            await PopulationAsync(population);
        }
        var path = $"histories/{environment.Provider}-{scenario.Id}-storage-checks.json";
        bundle.Write(path, checks.ToArray(), BenchJson.Default.CheckArray);
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
                    ["expected_status"] = DurableReader.Status(ExpectedStatus(scenario.Id)),
                    ["result_model"] = scenario.Id == "S01" ? "SHA256(UTF8(logical key)); no input transformation" : "no result",
                },
                BenchJson.Default.DictionaryStringString
            );
            await PrepareBackgroundAsync(plan, scenario, state, jobs, resources, ct, population);
            var due = Enumerable.Range(0, 12).Select(i => new BenchWork($"root-{i}")).ToArray();
            var ids = new List<long>();
            foreach (var work in due)
            {
                ids.Add(
                    (
                        await jobs.EnqueueAsync(
                            new EnqueueJobRequest(
                                "bench",
                                "work",
                                JobPayload.Json(work, WorkJson.Default.BenchWork),
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
            checks.AddRange(OutcomeOracle.Reconcile(WorkloadModel.Expected(due), observedDue));
            checks.AddRange(await CheckBackgroundAsync(state, scenario.Id, population, ct));
            bundle.Write($"histories/{tag}-rows.json", rows.ToArray(), TrialJson.Default.DurableRowArray);
            evidence.Add($"histories/{tag}-rows.json");
        }
    }

    internal static async Task<int> PrepareBackgroundAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider state,
        IJobs jobs,
        ResourceSampler resources,
        CancellationToken ct,
        int? population = null
    )
    {
        var size = population ?? BackgroundRoots(plan, scenario.Id);
        var future = DateTime.UtcNow.AddDays(1);
        for (var first = 0; first < size; first += 1000)
        {
            if (resources.BudgetExceeded)
            {
                throw new ResourceBudgetException("Background population exceeded its memory/disk budget.");
            }
            var batch = Enumerable
                .Range(first, Math.Min(1000, size - first))
                .Select(i =>
                {
                    var work = new BenchWork($"history-{i}", Signal: scenario.Id == "S02" ? "park" : null);
                    return new EnqueueJobRequest(
                        "bench",
                        "work",
                        JobPayload.Json(work, WorkJson.Default.BenchWork),
                        DeduplicationKey: work.Key,
                        Priority: scenario.Id == "S03" ? JobPriorityCode.High : JobPriorityCode.Normal,
                        NextRunAtUtc: scenario.Id == "S03" ? future : null
                    );
                })
                .ToArray();
            await jobs.EnqueueBatchAsync(batch, ct);
            if (first % 10000 == 0)
            {
                Console.WriteLine($"  {state.Provider} {scenario.Id}: background {first + batch.Length}/{size}");
            }
        }
        await plan.DrainAsync(
            async () => size - await PopulationCountAsync(state, ExpectedStatus(scenario.Id), ct),
            plan.Deadline(size, 0, state.DrainRate),
            ct
        );
        return size;
    }

    internal static async Task<Check[]> CheckBackgroundAsync(OwnedProvider state, string scenario, int population, CancellationToken ct)
    {
        var rows = (await new DurableReader(state).ReadAsync(null, ct))
            .Where(r => r.Key.StartsWith("history-", StringComparison.Ordinal))
            .ToArray();
        var seen = new BitArray(population);
        List<Check> checks = [];
        foreach (var row in rows)
        {
            if (
                !int.TryParse(row.Key.AsSpan(8), CultureInfo.InvariantCulture, out var ordinal)
                || ordinal < 0
                || ordinal >= population
                || seen[ordinal]
                || row.Status != ExpectedStatus(scenario)
            )
            {
                checks.Add(
                    new("background-identity-state", row.Key, false, "Unexpected, duplicate, resumed, or early-executed background job.")
                );
                continue;
            }
            seen[ordinal] = true;
            if (scenario == "S01" && row.Output != WorkloadModel.Expected([new(row.Key)])[0].Output)
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
                scenario,
                checks.Count == 0 && rows.Length == population,
                $"Exact {population}-identity background reconciled independently."
            )
        );
        return [.. checks];
    }

    private static async Task<CaseResult> RetentionAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
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
            await operations.Definitions.GetAsync("bench", "work", ct) ?? throw new InvalidOperationException("Missing work definition.");
        await operations.Definitions.UpdateOverridesAsync("bench", "work", definition.Version, new(JobRetentionSeconds: 0), ct: ct);
        using var worker = OwnedWorker.Start(state);
        await worker.ReadyAsync(ct);
        var input = new BenchWork(
            "root",
            Children: scenario.Id == "S05" ? 10 : 0,
            Signal: scenario.Id == "S04" ? "hold" : null,
            AfterChildrenSignal: scenario.Id == "S05" ? "hold" : null
        );
        var root = await jobs.EnqueueAsync(
            new EnqueueJobRequest("bench", "work", JobPayload.Json(input, WorkJson.Default.BenchWork), DeduplicationKey: input.Key),
            ct
        );
        await FaultCaseRunner.WaitAsync(
            async () => (await jobs.GetAsync(root, ct))?.Status == JobStatusCode.Suspended,
            plan.Deadline(1, 0, state.DrainRate),
            ct
        );
        List<Check> checks = [];
        var maintenance = host.Services.GetRequiredService<MaintenanceOperations>();
        var ns = (await operations.Namespaces.ListAsync(new(), ct)).Items.Single(n => n.JobNamespace == "bench").NamespaceId;
        var traffic = Enumerable.Range(0, 30).Select(i => new BenchWork($"traffic-{i}", WorkMs: 10)).ToArray();
        foreach (var work in traffic)
        {
            await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    "bench",
                    "retained-work",
                    JobPayload.Json(work, WorkJson.Default.BenchWork),
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
        checks.AddRange(OutcomeOracle.Reconcile(WorkloadModel.Expected(traffic), trafficActual));
        checks.Add(
            new(
                "live-parent-preserved",
                "root",
                before.Any(r => r.Key == "root" && r.Status == 20),
                "A waiting live root survives eligible-state retention."
            )
        );
        if (scenario.Id == "S05")
        {
            checks.Add(
                new(
                    "live-dependencies-preserved",
                    "root",
                    before.Count(r => r.ParentId == root.JobId) == 10,
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
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
        bundle.Write($"histories/{tag}-before.json", before.ToArray(), TrialJson.Default.DurableRowArray);
        var passed = checks.All(c => c.Passed);
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
            [$"histories/{tag}-checks.json", $"histories/{tag}-before.json"]
        );
    }

    /// <summary>The profile's background population: S02 parks it on a signal, S03 schedules it in the future.</summary>
    private static int BackgroundRoots(RunPlan plan, string scenario) =>
        (int)plan.ProfilePolicy(scenario == "S02" ? "parked_background_roots" : "future_background_roots");

    /// <summary>The status a history job settles in: S02's wait on a signal, S03's future due instant, S01's completion.</summary>
    private static int ExpectedStatus(string scenario) =>
        scenario switch
        {
            "S02" => 20,
            "S03" => 10,
            _ => 100,
        };

    private static async Task<long> PopulationCountAsync(OwnedProvider state, int status, CancellationToken ct)
    {
        await using var connection = await state.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var prefix = state.Provider == "sqlite" ? "main" : state.Schema;
        command.CommandText =
            $"SELECT COUNT(*) FROM {prefix}.jobs j JOIN {prefix}.runtimes r ON r.job_id=j.id WHERE j.deduplication_key LIKE 'history-%' AND r.status_code={status}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
