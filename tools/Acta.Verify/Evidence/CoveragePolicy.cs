namespace Acta.Verify;

/// <summary>
/// Checks that every planned scenario produced the results its runner owes on each database: each ladder pass climbed
/// until its first miss, a headline confirmed its rate, every fault repetition ran, and every measured trial ran the
/// topology its file writes. A result that is missing becomes a reconcile row, so a run never passes by omission.
/// </summary>
public static class CoveragePolicy
{
    public static CaseResult[] Missing(RunPlan plan, string provider, IEnumerable<CaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var rows = results.Where(r => r.Provider == provider && r.Phase != "reconcile").ToArray();
        List<CaseResult> missing = [];
        foreach (var scenario in plan.Scenarios)
        {
            var trials = rows.Where(r => r.Id == scenario.Id).ToArray();
            if (trials.Length == 0)
            {
                Add("No result for the planned scenario.");
                continue;
            }
            if (trials.All(r => r.Status == "NOT_APPLICABLE"))
            {
                if (ScenarioRunner.Unwritten(scenario, provider) is null)
                {
                    Add("The scenario did not run, and its file writes no reason it skips this database.");
                }
                continue;
            }
            switch (scenario.Runner)
            {
                case "rate-ladder":
                    Ladder(scenario, trials, Gap);
                    break;
                case "drain":
                    for (var pass = 1; pass <= ScenarioRunner.Passes(plan, scenario); pass++)
                    {
                        if (trials.FirstOrDefault(r => r.Phase == "measure" && r.Repetition == pass) is not { } drain)
                        {
                            Gap($"Drain pass {pass} has no result.");
                        }
                        else if (Verdicts.RateOutcome(drain) is not "held" and var outcome)
                        {
                            Gap($"Drain pass {pass} did not qualify: {outcome}.");
                        }
                    }
                    break;
                case "overload":
                    if (!trials.Any(r => r.Phase == "measure"))
                    {
                        Gap("The overload trial has no result.");
                    }
                    break;
                case "chaos":
                    var kills = scenario.Setting("busy_kills", provider);
                    var jobs = scenario.Jobs[provider];
                    if (
                        !trials.Any(r =>
                            r.Metrics.GetValueOrDefault("observed_busy_kills")?.Value >= kills
                            && r.Metrics.GetValueOrDefault("kills_with_reclaim")?.Value >= kills
                            && r.Metrics.GetValueOrDefault("acknowledged_roots")?.Value == jobs
                        )
                    )
                    {
                        Add($"Chaos did not show {kills} busy kills with reclaim over all {jobs} acknowledged jobs.");
                    }
                    break;
            }
            if (scenario.Group == "faults")
            {
                for (var repetition = 1; repetition <= ScenarioRunner.Repetitions(plan, scenario); repetition++)
                {
                    if (!trials.Any(r => r.Repetition == repetition))
                    {
                        Add($"Repetition {repetition} has no result.");
                    }
                }
            }
            foreach (var trial in trials.Where(r => r.Phase is "measure" or "confirm"))
            {
                if (Topology(scenario, provider, trial) is { } wrong)
                {
                    Add($"{trial.Phase} trial {trial.Repetition} ran {wrong}.");
                }
            }

            void Add(string reason) => missing.Add(CaseResult.Incomplete(scenario.Id, provider, "reconcile", reason));
            void Gap(string reason) => missing.Add(CaseResult.MeasurementGap(plan, scenario.Id, provider, "reconcile", reason));
        }
        return [.. missing];

        // Each pass climbs until its first rate that does not hold. The floor must hold in every pass where the suite
        // gates on measurement, and a headline confirms the highest rate held in every pass, once more one rate lower.
        void Ladder(Scenario scenario, CaseResult[] trials, Action<string> gap)
        {
            var rates = ScenarioRunner.LadderRates(plan, scenario, provider);
            var heldEverywhere = rates.Length;
            for (var pass = 1; pass <= ScenarioRunner.Passes(plan, scenario); pass++)
            {
                var held = 0;
                foreach (var rate in rates)
                {
                    var trial = trials.FirstOrDefault(r => r.Phase == "measure" && r.Repetition == pass && OfferedRate(r) == rate);
                    if (trial is null)
                    {
                        gap($"Pass {pass} has no result at {rate}/s.");
                        break;
                    }
                    var outcome = Verdicts.RateOutcome(trial);
                    if (outcome != "held")
                    {
                        // A harness error stops the climb short of the engine's limit wherever it lands.
                        if (outcome == "harness error")
                        {
                            gap($"Pass {pass} stopped on a harness error at {rate}/s.");
                        }
                        else if (held == 0 && outcome == "missed")
                        {
                            var reason = $"Pass {pass} missed the floor of {rate}/s.";
                            if (plan.MeasurementGatesVerdict)
                            {
                                missing.Add(Floor(scenario.Id, provider, reason));
                            }
                            else
                            {
                                gap(reason);
                            }
                        }
                        break;
                    }
                    held++;
                }
                heldEverywhere = Math.Min(heldEverywhere, held);
            }
            if (scenario.Headline is null || plan.Suite.ConfirmSeconds == 0 || heldEverywhere == 0)
            {
                return;
            }
            var confirmations = trials.Where(r => r.Phase == "confirm").ToArray();
            var top = confirmations.FirstOrDefault(r => OfferedRate(r) == rates[heldEverywhere - 1]);
            var lower = heldEverywhere > 1 ? confirmations.FirstOrDefault(r => OfferedRate(r) == rates[heldEverywhere - 2]) : null;
            if (top is null)
            {
                gap($"The confirmation at {rates[heldEverywhere - 1]}/s has no result.");
            }
            else if (Verdicts.RateOutcome(top) != "held" && (lower is null || Verdicts.RateOutcome(lower) != "held"))
            {
                gap("Neither confirmation held, so the scenario has no confirmed rate.");
            }
            foreach (var confirmation in confirmations.Where(c => Verdicts.RateOutcome(c) == "harness error"))
            {
                gap($"The confirmation at {OfferedRate(confirmation)}/s stopped on a harness error.");
            }
        }
    }

    private static double OfferedRate(CaseResult result) => result.Metrics.GetValueOrDefault("offered_rate")?.Value ?? 0;

    /// <summary>What a measured trial ran that its file does not write, or null when its topology matches.</summary>
    private static string? Topology(Scenario scenario, string provider, CaseResult trial)
    {
        double? Metric(string name) => trial.Metrics.GetValueOrDefault(name)?.Value;
        return Metric("worker_processes") != scenario.Workers[provider]
                ? $"{Metric("worker_processes")} workers, not {scenario.Workers[provider]}"
            : Metric("namespace_count") != scenario.Namespaces[provider]
                ? $"{Metric("namespace_count")} namespaces, not {scenario.Namespaces[provider]}"
            : scenario.Executors is { } executors && Metric("executors_per_worker") != executors
                ? $"{Metric("executors_per_worker")} executors per worker, not {executors}"
            : Metric("batch_size") != scenario.JobsPerCall ? $"{Metric("batch_size")} jobs per call, not {scenario.JobsPerCall}"
            : null;
    }

    /// <summary>A ladder that missed its first rate where the suite gates on measurement: performance fails.</summary>
    private static CaseResult Floor(string id, string provider, string reason) =>
        new(id, provider, "reconcile", 0, "FAIL", reason, "NOT_APPLICABLE", "NOT_APPLICABLE", "COMPLETE", "MISSED", [], []);
}
