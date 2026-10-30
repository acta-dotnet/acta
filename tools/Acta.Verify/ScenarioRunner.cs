namespace Acta.Verify;

/// <summary>What a runner is given: the plan, its scenario, the database, the bundle, and where it records results.</summary>
internal sealed record ScenarioRun(
    RunPlan Plan,
    Scenario Scenario,
    OwnedProvider Environment,
    EvidenceBundle Bundle,
    Func<CaseResult, Task<CaseResult>> Record,
    CancellationToken Ct
)
{
    public string Provider => Environment.Provider;
}

/// <summary>
/// Runs a plan's scenarios on one database, each by the runner its file names. A runner is the only place a scenario's
/// behaviour lives in code: its workload, topology, rates and target come from the file.
/// </summary>
public static class ScenarioRunner
{
    private static readonly Dictionary<string, Func<ScenarioRun, Task>> Runners = new(StringComparer.Ordinal)
    {
        ["rate-ladder"] = RateLadderAsync,
        ["drain"] = DrainAsync,
        ["overload"] = OverloadAsync,
        ["verify"] = r => RepeatAsync(r, n => TrialRunner.VerifyAsync(r, n)),
        ["fault"] = r => RepeatAsync(r, n => FaultCaseRunner.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, n, r.Ct)),
        ["chaos"] = async r => await r.Record(await ChaosCase.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct)),
        ["storage"] = async r => await r.Record(await StorageCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct)),
        ["scheduling"] = async r => await r.Record(await SchedulingCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct)),
        ["race"] = r => RepeatAsync(r, n => RaceCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["transaction"] = r => RepeatAsync(r, n => TransactionCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["registration"] = r => RepeatAsync(r, n => RegistrationCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["reply-loss"] = r => RepeatAsync(r, n => ReplyLossCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["connectivity"] = r => RepeatAsync(r, n => ConnectivityCase.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["stale-attempt"] = r => RepeatAsync(r, n => StaleAttemptCase.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["timeout-wakeup"] = r => RepeatAsync(r, n => TimeoutAndWakeupCases.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
        ["outbox"] = r => RepeatAsync(r, n => OutboxCase.RunAsync(r.Plan, r.Scenario, r.Environment, r.Bundle, r.Ct, n)),
    };

    // Every scenario writes these; a runner missing from the tables below reads nothing else.
    private static readonly string[] CommonFields = ["title", "question", "runner", "skip"];
    private static readonly string[] LoadFields =
    [
        "work",
        "workers",
        "executors",
        "namespaces",
        "calls",
        "jobs_per_call",
        "lanes",
        "lane",
        "concurrency_key",
    ];

    /// <summary>The fields each runner reads beyond the common ones.</summary>
    private static readonly Dictionary<string, string[]> FieldNames = new(StringComparer.Ordinal)
    {
        ["rate-ladder"] = [.. LoadFields, "headline", "rates", "seconds", "admission_only", "p99_ms", "settings"],
        ["drain"] = [.. LoadFields, "headline", "jobs"],
        ["overload"] = [.. LoadFields, "rates", "seconds", "p99_ms", "settings"],
        ["verify"] = ["work", "jobs", "lanes", "lane", "concurrency_key", "settings"],
        ["fault"] = ["work"],
        ["chaos"] = ["jobs", "workers", "executors", "seconds", "settings"],
        ["storage"] = ["settings"],
    };

    /// <summary>The settings each runner reads.</summary>
    private static readonly Dictionary<string, string[]> SettingNames = new(StringComparer.Ordinal)
    {
        ["rate-ladder"] = ["background", "background_state", "queries_per_second"],
        ["overload"] = ["overload_rate", "recovery_rate"],
        ["verify"] = ["send_signal", "signal_copies"],
        ["chaos"] = ["step_ms", "busy_kills"],
        ["storage"] = ["populations"],
    };

    // The harness registers the work definition in these namespaces only.
    private static readonly string[] ChildNamespaces = ["verify", "verify-other"];

    /// <summary>Whether the <paramref name="runner"/> runner reads the scenario field <paramref name="field"/>.</summary>
    public static bool Reads(string runner, string field) =>
        CommonFields.Contains(field) || FieldNames.GetValueOrDefault(runner, []).Contains(field);

    /// <summary>The runners a scenario file may name.</summary>
    public static IReadOnlyCollection<string> Names => Runners.Keys;

    /// <summary>
    /// Refuses a scenario whose runner does not exist, that writes a setting its runner does not read, or that asks for
    /// more namespaces than the harness registers.
    /// </summary>
    public static void Validate(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (!Runners.ContainsKey(scenario.Runner))
        {
            throw new InvalidOperationException($"Scenario {scenario.Id} names the runner {scenario.Runner}, which does not exist.");
        }
        if (scenario.Settings.Keys.FirstOrDefault(k => !SettingNames.GetValueOrDefault(scenario.Runner, []).Contains(k)) is { } unread)
        {
            throw new InvalidOperationException(
                $"Scenario {scenario.Id} writes the setting {unread}, which the {scenario.Runner} runner does not read."
            );
        }
        if (scenario.Namespaces.Values.Any(n => n is < 1 or > 2) || scenario.Work.Any(w => !ChildNamespaces.Contains(w.ChildNamespace)))
        {
            throw new InvalidOperationException(
                $"Scenario {scenario.Id} writes namespaces the harness does not register: one or two, and children in {string.Join(" or ", ChildNamespaces)}."
            );
        }
    }

    /// <summary>
    /// A run's phases in order: performance measures one database at a time on a quiet box, then correctness, faults
    /// and chaos run on every database at once, since they judge outcomes rather than time.
    /// </summary>
    public static readonly string[] Phases = ["performance", "correctness"];

    /// <summary>The phase <paramref name="scenario"/> runs in.</summary>
    public static string PhaseOf(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Group == "performance" ? "performance" : "correctness";
    }

    /// <summary>The passes a measured scenario owes: the suite's headline passes for a headline, its passes otherwise.</summary>
    public static int Passes(RunPlan plan, Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Headline is null ? plan.Suite.Passes : plan.Suite.HeadlinePasses;
    }

    /// <summary>The rates a ladder climbs on <paramref name="provider"/>: the written ones, or the first only where the suite says so.</summary>
    public static int[] LadderRates(RunPlan plan, Scenario scenario, string provider)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        var rates = scenario.Rates[provider];
        return plan.Suite.FirstRateOnly ? rates[..1] : rates;
    }

    /// <summary>A measured window: the scenario's own, else the suite's for a ladder of <paramref name="rates"/> rates.</summary>
    public static int Window(RunPlan plan, Scenario scenario, int rates)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Seconds ?? (rates > 1 ? plan.Suite.LadderSeconds : plan.Suite.SingleSeconds);
    }

    /// <summary>The repetitions of one bounded trial: the suite's for a fault scenario, one otherwise.</summary>
    public static int Repetitions(RunPlan plan, Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Group == "faults" ? plan.Suite.FaultRepetitions : 1;
    }

    /// <summary>The enqueue calls a measured trial keeps in flight: the scenario's, else the harness's for the database.</summary>
    public static int Calls(RunPlan plan, Scenario scenario, string provider)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Calls.TryGetValue(provider, out var calls) ? calls : (int)plan.Policy("measurement", "producer_calls", provider);
    }

    /// <summary>The jobs a verification trial submits: the scenario's cohort, else the harness's verification count.</summary>
    public static int VerificationJobs(RunPlan plan, Scenario scenario, string provider)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Jobs.GetValueOrDefault(provider, (int)plan.Policy("measurement", "verification_jobs"));
    }

    /// <summary>
    /// Runs <paramref name="scenarios"/> on one database in order, printing before each how far the database is and
    /// about how long its remaining scenarios take by <paramref name="estimate"/>. A scenario that skips the database,
    /// or writes no load for it, records why and does not run.
    /// </summary>
    internal static async Task RunAsync(
        RunPlan plan,
        IReadOnlyList<Scenario> scenarios,
        OwnedProvider environment,
        EvidenceBundle bundle,
        Action<CaseResult> emit,
        Func<Scenario, double> estimate,
        CancellationToken ct
    )
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var lastRecorded = started;
        for (var index = 0; index < scenarios.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var scenario = scenarios[index];
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMinutes;
            var left = scenarios.Skip(index).Sum(estimate) / 60;
            Console.WriteLine(
                $"[{environment.Provider} {index + 1}/{scenarios.Count} {scenario.Id} | {elapsed:F0} min in | about {left:F0} min left]"
            );
            var run = new ScenarioRun(plan, scenario, environment, bundle, RecordAsync, ct);
            if (Unwritten(scenario, environment.Provider) is { } skipped)
            {
                await RecordAsync(CaseResult.NotApplicable(scenario.Id, environment.Provider, skipped));
                continue;
            }
            try
            {
                await Runners[scenario.Runner](run);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ResourceBudgetException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = $"Scenario did not finish ({ex.GetType().Name}: {ex.Message}); partial histories retained.";
                Console.WriteLine($"  {scenario.Id} {environment.Provider}: {reason}");
                File.WriteAllText(bundle.Resolve($"logs/{environment.Provider}-{scenario.Id}-error.txt"), ex.ToString());
                await RecordAsync(CaseResult.Incomplete(scenario.Id, environment.Provider, "verify", reason));
            }
        }

        async Task<CaseResult> RecordAsync(CaseResult result)
        {
            var trials = environment.TakeTrials();
            result = await LedgerValidation.ApplyAsync(result, trials, bundle, ct);
            // Results are recorded one after another, so the time since the previous one is the time this one took.
            var recorded = System.Diagnostics.Stopwatch.GetTimestamp();
            result.Metrics["wall_seconds"] = new(
                (recorded - lastRecorded) / (double)System.Diagnostics.Stopwatch.Frequency,
                "seconds",
                Reason: "Time from the previous recorded result to this one, its setup, trial and ledger checks included."
            );
            lastRecorded = recorded;
            // A measured drain sets the pace the liveness deadline of later waits allows.
            if (
                result is { Phase: "measure", Status: "PASS" }
                && result.Metrics.GetValueOrDefault("backlog_drain_rate")?.Value is { } drain
            )
            {
                environment.DrainRate = Math.Min(environment.DrainRate ?? drain, drain);
            }
            emit(result);
            // A trial that passed, or missed only its rate, leaves nothing to inspect: dropping its schema keeps later
            // trials on a database holding their own rows only. A failure keeps its schema, and so does a failed drop,
            // which the database's own cleanup removes.
            if (result.Status == "PASS")
            {
                foreach (var trial in trials)
                {
                    try
                    {
                        await trial.DropAsync(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.WriteLine($"  {result.Id} {result.Provider}: schema drop failed ({ex.GetType().Name}: {ex.Message}).");
                    }
                }
            }
            return result;
        }
    }

    /// <summary>Why <paramref name="scenario"/> does not run on <paramref name="provider"/>, or null when it does.</summary>
    public static string? Unwritten(Scenario scenario, string provider) =>
        scenario.Skip.TryGetValue(provider, out var skipped) ? skipped
        : scenario.Runner is "rate-ladder" or "overload" && !scenario.Rates.ContainsKey(provider)
            ? $"The scenario writes no rate for {provider}."
        : scenario.Runner == "drain" && !scenario.Jobs.ContainsKey(provider) ? $"The scenario writes no cohort for {provider}."
        : null;

    /// <summary>
    /// Each pass climbs the written rates in order and stops at the first that misses. A rate held in every pass is the
    /// result, and a headline confirms the highest such rate once over the suite's confirmation window, stepping down one
    /// rate if that misses.
    /// </summary>
    private static async Task RateLadderAsync(ScenarioRun run)
    {
        var suite = run.Plan.Suite;
        var rates = LadderRates(run.Plan, run.Scenario, run.Provider);
        var seconds = Window(run.Plan, run.Scenario, rates.Length);
        var heldEverywhere = rates.Length;
        for (var pass = 1; pass <= Passes(run.Plan, run.Scenario); pass++)
        {
            var held = 0;
            foreach (var rate in rates)
            {
                var result = await run.Record(
                    await LoadTrial.RunAsync(run.Plan, run.Environment, run.Bundle, Spec(run, "measure", pass, rate, seconds), run.Ct)
                );
                if (!Held(result))
                {
                    break;
                }
                held++;
            }
            heldEverywhere = Math.Min(heldEverywhere, held);
        }
        if (run.Scenario.Headline is null || suite.ConfirmSeconds == 0)
        {
            return;
        }
        for (var step = heldEverywhere - 1; step >= Math.Max(0, heldEverywhere - 2); step--)
        {
            var confirmed = await run.Record(
                await LoadTrial.RunAsync(
                    run.Plan,
                    run.Environment,
                    run.Bundle,
                    Spec(run, "confirm", 1, rates[step], suite.ConfirmSeconds),
                    run.Ct
                )
            );
            if (Held(confirmed))
            {
                return;
            }
        }
    }

    private static async Task DrainAsync(ScenarioRun run)
    {
        for (var pass = 1; pass <= Passes(run.Plan, run.Scenario); pass++)
        {
            await run.Record(
                await LoadTrial.RunAsync(
                    run.Plan,
                    run.Environment,
                    run.Bundle,
                    Spec(run, "measure", pass, 0, 0) with
                    {
                        Mode = LoadMode.Cohort,
                        Segments = [],
                        Cohort = run.Scenario.Jobs[run.Provider],
                        WarmupSeconds = 0,
                    },
                    run.Ct
                )
            );
        }
    }

    /// <summary>Offers the written rate through a warmup, the overload rate for half the window, then the recovery rate.</summary>
    private static async Task OverloadAsync(ScenarioRun run)
    {
        var suite = run.Plan.Suite;
        var rate = run.Scenario.Rates[run.Provider][0];
        var seconds = Window(run.Plan, run.Scenario, 1);
        var overload = run.Scenario.Setting("overload_rate", run.Provider);
        var recovery = run.Scenario.Setting("recovery_rate", run.Provider);
        await run.Record(
            await LoadTrial.RunAsync(
                run.Plan,
                run.Environment,
                run.Bundle,
                Spec(run, "measure", 1, rate, seconds) with
                {
                    Segments = [new(suite.WarmupSeconds, rate), new(seconds / 2, overload), new(seconds - seconds / 2, recovery)],
                    OverloadRate = overload,
                },
                run.Ct
            )
        );
    }

    private static bool Held(CaseResult result) => Verdicts.RateOutcome(result) == "held";

    private static async Task RepeatAsync(ScenarioRun run, Func<int, Task<CaseResult>> trial)
    {
        for (var repetition = 1; repetition <= Repetitions(run.Plan, run.Scenario); repetition++)
        {
            await run.Record(await trial(repetition));
        }
    }

    /// <summary>The measured trial of <paramref name="run"/>'s scenario at <paramref name="rate"/>, for its database.</summary>
    private static LoadSpec Spec(ScenarioRun run, string phase, int pass, double rate, int seconds)
    {
        var (plan, scenario, provider) = (run.Plan, run.Scenario, run.Provider);
        var background = (int)(scenario.SettingOrNull("background", provider) ?? 0);
        // Built once a trial, so an arrival picks its lane without formatting a name.
        string?[] lanes = scenario.Lanes > 0 ? [.. Enumerable.Range(0, scenario.Lanes).Select(scenario.LaneAt)] : [scenario.Lane];
        return new(
            scenario.Id,
            phase,
            pass,
            rate,
            scenario.AdmissionOnly ? LoadMode.Admission : LoadMode.Stream,
            [new(plan.Suite.WarmupSeconds + seconds, rate)],
            0,
            plan.Suite.WarmupSeconds,
            seconds,
            Calls(plan, scenario, provider),
            scenario.JobsPerCall,
            scenario.Workers[provider],
            scenario.Executors,
            scenario.Namespaces[provider],
            scenario.P99Ms ?? plan.Policy("measurement", "default_p99_ms"),
            i => scenario.WorkAt(i, plan.Seed),
            i => lanes[i % lanes.Length],
            scenario.ConcurrencyKey,
            scenario.NeedsEffectService,
            scenario.CrossNamespace,
            scenario.SettingOrNull("queries_per_second", provider) ?? 0,
            scenario.Work.Count == 1 ? scenario.Work[0].PayloadBytes : null,
            0,
            background > 0
                ? (jobs, state, resources, ct) =>
                    StorageCases.PrepareBackgroundAsync(plan, scenario, state, jobs, resources, background, ct)
                : null,
            background > 0 ? (state, population, ct) => StorageCases.CheckBackgroundAsync(state, scenario, population, ct) : null
        );
    }
}
