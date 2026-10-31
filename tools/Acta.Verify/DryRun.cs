using System.Globalization;
using System.Text;

namespace Acta.Verify;

/// <summary>
/// What a plan would run, before it runs: per database and scenario the most trials and jobs it can take, and a time
/// estimate. A scenario's time comes from the newest bundle under the output directory that ran it on that database,
/// its setup and verification per trial plus this plan's windows; a scenario no bundle has is estimated and marked so.
/// </summary>
internal static class DryRun
{
    // Seconds a trial spends outside its window when no earlier bundle measured it: schema, host, workers, the drain
    // after the window, the paged verification and the ledger checks.
    private const double AssumedTrialOverhead = 25;

    /// <summary>
    /// The plan as a table per database, with the time of each phase: performance runs one database at a time, so its
    /// times add up; correctness runs every database at once, so the slowest one sets its time.
    /// </summary>
    public static string Describe(RunPlan plan, string output)
    {
        var history = History(output);
        var text = new StringBuilder().Append(
            CultureInfo.InvariantCulture,
            $"{plan.SuiteName}: {plan.Scenarios.Count} scenarios on {string.Join(", ", plan.Providers)}, at most:\n"
        );
        Dictionary<string, double> phases = new(StringComparer.Ordinal);
        foreach (var provider in plan.Providers)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{provider}\n");
            foreach (var phase in ScenarioRunner.Phases)
            {
                double seconds = 0;
                foreach (var scenario in plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) == phase))
                {
                    if (ScenarioRunner.Unwritten(scenario, provider) is { } skipped)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"  {scenario.Id, -40} skipped: {skipped}\n");
                        continue;
                    }
                    var (trials, jobs, _) = Size(plan, scenario, provider);
                    var (estimate, measured) = Estimate(plan, scenario, provider, history);
                    seconds += estimate;
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"  {scenario.Id, -40} {trials, 4} trials {(jobs is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "-"), 12} jobs {estimate / 60, 7:F1} min{(measured ? "" : " (assumed)")}\n"
                    );
                }
                text.Append(CultureInfo.InvariantCulture, $"  {phase, -40} {seconds / 60, 38:F1} min\n");
                phases[phase] =
                    phase == "correctness" ? Math.Max(phases.GetValueOrDefault(phase), seconds) : phases.GetValueOrDefault(phase) + seconds;
            }
        }
        var total = phases.Values.Sum();
        text.Append(
            CultureInfo.InvariantCulture,
            $"\nPerformance, one database at a time: {phases.GetValueOrDefault("performance") / 60:F0} min. Correctness, every database at once: {phases.GetValueOrDefault("correctness") / 60:F0} min. Chaos, one database at a time: {phases.GetValueOrDefault("chaos") / 60:F0} min.\n"
                + $"Estimated {total / 3600:F1} h; the release target is 3 h and the ceiling 10 h. Times marked assumed have no earlier bundle under {output}.\n"
        );
        return text.ToString();
    }

    /// <summary>
    /// About how many seconds <paramref name="scenario"/> takes on <paramref name="provider"/>, and whether an earlier
    /// bundle measured its overhead.
    /// </summary>
    public static (double Seconds, bool Measured) Estimate(
        RunPlan plan,
        Scenario scenario,
        string provider,
        IReadOnlyList<CaseResult> history
    )
    {
        if (ScenarioRunner.Unwritten(scenario, provider) is not null)
        {
            return (0, true);
        }
        var (trials, _, windows) = Size(plan, scenario, provider);
        var past = history.Where(r => r.Id == scenario.Id && r.Provider == provider && r.Phase != "reconcile").ToArray();
        return (trials * (past.Length == 0 ? AssumedTrialOverhead : past.Average(r => Wall(r) - Window(r))) + windows, past.Length > 0);
    }

    /// <summary>
    /// The most trials a scenario runs on a database, the jobs they enqueue (null where a bespoke runner sets them in
    /// code), and the seconds their windows offer load.
    /// </summary>
    private static (int Trials, long? Jobs, double Seconds) Size(RunPlan plan, Scenario scenario, string provider)
    {
        var suite = plan.Suite;
        var passes = ScenarioRunner.Passes(plan, scenario);
        switch (scenario.Runner)
        {
            case "rate-ladder":
                var rates = ScenarioRunner.LadderRates(plan, scenario, provider);
                var window = suite.WarmupSeconds + ScenarioRunner.Window(plan, scenario, rates.Length);
                var confirmations = scenario.Headline is null || suite.ConfirmSeconds == 0 ? 0 : Math.Min(2, rates.Length);
                var confirm = suite.WarmupSeconds + suite.ConfirmSeconds;
                return (
                    passes * rates.Length + confirmations,
                    passes * rates.Sum(r => (long)r * window) + (long)rates[^1] * confirm * confirmations,
                    passes * rates.Length * window + confirmations * confirm
                );
            case "drain":
                return (passes, (long)passes * scenario.Jobs[provider], 0);
            case "overload":
                var seconds = ScenarioRunner.Window(plan, scenario, 1);
                return (
                    1,
                    (long)(
                        scenario.Rates[provider][0] * suite.WarmupSeconds
                        + (scenario.Setting("overload_rate", provider) + scenario.Setting("recovery_rate", provider)) * seconds / 2
                    ),
                    suite.WarmupSeconds + seconds
                );
            case "chaos":
                return (1, scenario.Jobs[provider], scenario.Seconds ?? 0);
            case "verify":
                return (1, ScenarioRunner.VerificationJobs(plan, scenario, provider), 0);
            case "storage" when scenario.Settings.ContainsKey("populations"):
                var populations = scenario.SettingList("populations", provider);
                return (populations.Length, populations.Sum(p => (long)p + ScenarioRunner.VerificationJobs(plan, scenario, provider)), 0);
            default:
                return (ScenarioRunner.Repetitions(plan, scenario), null, 0);
        }
    }

    private static double Wall(CaseResult result) => result.Metrics.GetValueOrDefault("wall_seconds")?.Value ?? AssumedTrialOverhead;

    // The chaos window holds its own warm-up; a measured trial's warm-up precedes its window.
    private static double Window(CaseResult result) =>
        result.Metrics.GetValueOrDefault("chaos_window_seconds")?.Value
        ?? (result.Metrics.GetValueOrDefault("warmup_seconds")?.Value ?? 0)
            + (result.Metrics.GetValueOrDefault("scheduled_window_seconds")?.Value ?? 0);

    /// <summary>The results of the newest bundle under <paramref name="output"/>, or none.</summary>
    public static List<CaseResult> History(string output) =>
        Directory.Exists(output)
        && Directory
            .EnumerateDirectories(output)
            .Select(d => Path.Combine(d, "cases.jsonl"))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault()
            is { } journal
            ? EvidenceBundle.ReadCases(journal).Results
            : [];
}
