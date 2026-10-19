namespace Acta.Bench;

// Execution recipes may combine verification and reconciliation in one fault case.
// Catalogue presence alone never establishes that the declared measurements ran.
public static class CoveragePolicy
{
    public static bool IsCanonical(RunPlan plan, string id) =>
        plan
            .FullPolicy.GetProperty("measurement")
            .GetProperty("canonical_measured_case_ids")
            .EnumerateArray()
            .Any(e => e.GetString() == id);

    /// <summary>The profile's validation fractions of the best qualifying discovery rate, frozen before measurement.</summary>
    public static double[] SelectedRates(RunPlan plan, IEnumerable<CaseResult> results)
    {
        var best = BestDiscoveryRate(results);
        return best <= 0
            ? []
            : plan
                .FullPolicy.GetProperty("profiles")
                .GetProperty(plan.Profile)
                .GetProperty("validation_load_fractions")
                .EnumerateArray()
                .Select(f => (double)Math.Max(1, (int)(best * f.GetDouble())))
                .Distinct()
                .ToArray();
    }

    /// <summary>
    /// The lowest discovery rate above the best qualifying one that did not qualify, or the protocol's multiple of the
    /// best qualifying rate when every attempted rate qualified.
    /// </summary>
    public static double OverloadRate(RunPlan plan, IEnumerable<CaseResult> results)
    {
        var best = BestDiscoveryRate(results);
        return Discovery(results)
            .Where(r => !IsDiscoveryCandidate(r))
            .Select(OfferedRate)
            .Where(rate => rate > best)
            .DefaultIfEmpty(best * plan.Policy("capacity", "overload", "rate_multiple_without_a_failed_discovery_rate"))
            .Min();
    }

    /// <summary>
    /// The rate E01 and E03 derive their load from: the prerequisite case's validated capacity or, when none validated
    /// and the profile does not gate its verdict on measurement, its best qualifying discovery rate. Zero when neither
    /// exists.
    /// </summary>
    public static double PrerequisiteRate(RunPlan plan, IEnumerable<CaseResult> results)
    {
        var validated = ValidatedCapacity(plan, results);
        return validated > 0 || plan.MeasurementGatesVerdict ? validated : BestDiscoveryRate(results);
    }

    private static double BestDiscoveryRate(IEnumerable<CaseResult> results) =>
        Discovery(results).Where(IsDiscoveryCandidate).Select(OfferedRate).DefaultIfEmpty(0).Max();

    private static IEnumerable<CaseResult> Discovery(IEnumerable<CaseResult> results) =>
        results.Where(r => r.Phase == "discover" && r.Metrics.GetValueOrDefault("diagnostic_cell")?.Value != 1);

    private static double OfferedRate(CaseResult result) => result.Metrics.GetValueOrDefault("offered_rate")?.Value ?? 0;

    public static bool IsDiscoveryCandidate(CaseResult result) =>
        result.Phase == "discover"
        && result.Status == "PASS"
        && (
            result.Measurement == "VALID"
            || result.Measurement == "UNSTABLE" && result.Metrics.GetValueOrDefault("calibration_candidate")?.Value == 1
        );

    public static double ValidatedCapacity(RunPlan plan, IEnumerable<CaseResult> results) =>
        results
            .Where(r => r.Phase == "measure" && r.Metrics.GetValueOrDefault("diagnostic_cell")?.Value != 1)
            .GroupBy(r => r.Metrics.GetValueOrDefault("offered_rate")?.Value)
            .Where(g => g.Key > 0 && g.All(r => r.Status == "PASS" && r.Measurement == "VALID") && RepetitionsComplete(plan, g))
            .Select(g => g.Key!.Value)
            .DefaultIfEmpty(0)
            .Max();

    public static bool RepetitionsComplete(RunPlan plan, IEnumerable<CaseResult> results)
    {
        var rows = results.ToArray();
        return Enumerable.Range(1, plan.Repetitions).All(i => rows.Any(r => r.Repetition == i))
            && (
                !plan.IsExtended
                || !PerformanceSummary.IsVariable(plan, rows.Where(r => r.Repetition <= plan.Repetitions))
                || Enumerable.Range(plan.Repetitions + 1, plan.AdditionalRepetitions).All(i => rows.Any(r => r.Repetition == i))
            );
    }

    /// <summary>The diagnostic cells the runner executes for a case, and so exactly the cells coverage requires.</summary>
    public static IEnumerable<TrialCell> RequiredCells(RunPlan plan, string provider, string id) =>
        plan.DiagnosticCells.Where(c =>
            c.CaseId == id
            && (provider != "sqlite" || c.Workers == 1)
            && (plan.IsExtended || plan.RequestedDiagnosticCells.Contains(c.Name, StringComparer.Ordinal))
        );

    public static CaseResult[] Missing(RunPlan plan, string provider, IEnumerable<CaseResult> results)
    {
        var rows = results.Where(r => r.Provider == provider).ToArray();
        List<CaseResult> missing = [];
        foreach (var scenario in plan.Cases.Where(c => c.Family != "harness"))
        {
            var cases = rows.Where(r => r.Id == scenario.Id && r.Phase != "reconcile").ToArray();
            if (cases.Length == 0)
            {
                Add("No execution evidence for the requested case.");
                continue;
            }
            if (cases.All(r => r.Status == "NOT_APPLICABLE"))
            {
                if (!(scenario.Id == "D04" || provider == "sqlite" && scenario.Id is "F04" or "F05"))
                {
                    Add("Case was not applicable without a predeclared provider exception.");
                }
                continue;
            }
            if (!cases.Any(r => r.Phase is "verify" or "fault" or "soak"))
            {
                Add("No verification/reconciliation execution accompanies the measurement.");
            }
            if (scenario.Id == "F09")
            {
                var requiredKills = plan.ProfilePolicy("chaos_minimum_busy_kills");
                if (
                    !cases.Any(r =>
                        r.Metrics.GetValueOrDefault("observed_busy_kills")?.Value >= requiredKills
                        && r.Metrics.GetValueOrDefault("kills_with_reclaim")?.Value >= requiredKills
                        && r.Metrics.GetValueOrDefault("acknowledged_roots")?.Value == plan.ChaosJobs
                    )
                )
                {
                    Add("Required seeded busy kills, reclaim evidence, or acknowledged cohort accounting is incomplete.");
                }
            }
            if (
                scenario.Id == "E01"
                && !cases.Any(r =>
                    r.Phase == "soak"
                    && (r.Measurement == "VALID" || !plan.MeasurementGatesVerdict)
                    && r.Metrics.GetValueOrDefault("scheduled_window_seconds")?.Value >= plan.SoakSeconds
                )
            )
            {
                Add("Required sustained soak interval or measurement validity is incomplete.");
            }
            if (plan.IsExtended && scenario.Phases.Contains("fault", StringComparer.Ordinal) && scenario.Id != "F09")
            {
                for (var trial = 1; trial <= plan.TargetedFaultRepetitions; trial++)
                {
                    if (!cases.Any(r => r.Repetition == trial && r.Phase is "fault" or "verify"))
                    {
                        Add($"Missing required boundary repetition {trial}.");
                    }
                }
            }
            if (!IsCanonical(plan, scenario.Id))
            {
                continue;
            }
            foreach (var cell in RequiredCells(plan, provider, scenario.Id))
            {
                for (var trial = 1; trial <= plan.Repetitions; trial++)
                {
                    if (
                        !cases.Any(r =>
                            r.Phase == "measure"
                            && r.DiagnosticCellName == cell.Name
                            && r.Repetition == trial
                            && r.Metrics.GetValueOrDefault("diagnostic_cell")?.Value == 1
                            && (cell.Executors is null || r.Metrics.GetValueOrDefault("executors_per_worker")?.Value == cell.Executors)
                            && r.Metrics.GetValueOrDefault("worker_processes")?.Value == cell.Workers
                            && r.Metrics.GetValueOrDefault("namespace_count")?.Value == cell.Namespaces
                            && (cell.PayloadBytes is null || r.Metrics.GetValueOrDefault("payload_bytes")?.Value == cell.PayloadBytes)
                            && (cell.BatchSize is null || r.Metrics.GetValueOrDefault("batch_size")?.Value == cell.BatchSize)
                        )
                    )
                    {
                        AddMeasurementGap($"Missing diagnostic cell {cell.Name}, independent trial {trial}.");
                    }
                }
                var cellTrials = cases.Where(r => r.Phase == "measure" && r.DiagnosticCellName == cell.Name).ToArray();
                if (
                    plan.IsExtended
                    && PerformanceSummary.IsVariable(plan, cellTrials.Where(r => r.Repetition <= plan.Repetitions))
                    && !RepetitionsComplete(plan, cellTrials)
                )
                {
                    AddMeasurementGap(
                        $"Predeclared dispersion rule requires {plan.AdditionalRepetitions} additional retained trials for diagnostic cell {cell.Name}."
                    );
                }
            }
            var canonical = cases.Where(r => r.Metrics.GetValueOrDefault("diagnostic_cell")?.Value != 1).ToArray();
            if (scenario.Id == "L04")
            {
                for (var trial = 1; trial <= plan.Repetitions; trial++)
                {
                    if (
                        !canonical.Any(r => r.Phase == "measure" && r.Repetition == trial && r.Measurement == "VALID" && r.Status == "PASS")
                    )
                    {
                        AddMeasurementGap(
                            $"Missing valid fixed-cohort backlog-drain trial {trial}; this workload has no offered arrival rate."
                        );
                    }
                }
                if (
                    plan.IsExtended
                    && PerformanceSummary.IsVariable(plan, canonical.Where(r => r.Phase == "measure" && r.Repetition <= plan.Repetitions))
                    && !RepetitionsComplete(plan, canonical.Where(r => r.Phase == "measure"))
                )
                {
                    AddMeasurementGap(
                        $"Predeclared dispersion rule requires {plan.AdditionalRepetitions} additional retained backlog-drain trials."
                    );
                }
                continue;
            }
            var rates = SelectedRates(plan, canonical);
            if (rates.Length == 0)
            {
                AddMeasurementGap("Capacity discovery did not find a valid arrival rate.");
                continue;
            }
            foreach (var rate in rates)
            {
                for (var trial = 1; trial <= plan.Repetitions; trial++)
                {
                    if (
                        !canonical.Any(r =>
                            r.Phase == "measure" && r.Repetition == trial && r.Metrics.GetValueOrDefault("offered_rate")?.Value == rate
                        )
                    )
                    {
                        AddMeasurementGap($"Missing fresh measured trial {trial} at the frozen selected rate {rate}.");
                    }
                }
                var trials = canonical
                    .Where(r => r.Phase == "measure" && r.Metrics.GetValueOrDefault("offered_rate")?.Value == rate)
                    .ToArray();
                if (
                    plan.IsExtended
                    && PerformanceSummary.IsVariable(plan, trials.Where(r => r.Repetition <= plan.Repetitions))
                    && Enumerable.Range(plan.Repetitions + 1, plan.AdditionalRepetitions).Any(i => !trials.Any(r => r.Repetition == i))
                )
                {
                    AddMeasurementGap(
                        $"Predeclared dispersion rule requires {plan.AdditionalRepetitions} additional retained trials at rate {rate}."
                    );
                }
            }
            if (ValidatedCapacity(plan, canonical) <= 0)
            {
                AddMeasurementGap(
                    "No arrival rate passed all required independent validation trials; correctness results remain separate."
                );
            }

            void Add(string reason) => missing.Add(CaseResult.Incomplete(scenario.Id, provider, "reconcile", reason));
            void AddMeasurementGap(string reason) =>
                missing.Add(CaseResult.MeasurementGap(plan, scenario.Id, provider, "reconcile", reason));
        }
        return [.. missing];
    }
}
