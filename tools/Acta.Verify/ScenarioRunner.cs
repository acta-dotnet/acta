using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class ScenarioRunner
{
    private static readonly HashSet<string> Implemented = new(StringComparer.Ordinal)
    {
        "L01",
        "L02",
        "L03",
        "L04",
        "C01",
        "C02",
        "P01",
        "P02",
        "P03",
        "P06",
        "P08",
        "R01",
        "R02",
        "M01",
        "M02",
        "M03",
        "M04",
        "T02",
        "T04",
        "T06",
        "E02",
        "I05",
        "S02",
        "S03",
    };

    public static async Task RunAsync(
        RunPlan plan,
        OwnedProvider environment,
        EvidenceBundle bundle,
        Action<CaseResult> emit,
        CancellationToken ct
    )
    {
        List<CaseResult> completed = [];
        var lastRecorded = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var descriptor in plan.Cases.Where(c => c.Family != "harness"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (descriptor.Id is "E01" or "E03")
                {
                    var prerequisite = descriptor.Id == "E01" ? "M03" : "L01";
                    var prerequisiteRows = completed.Where(r => r.Id == prerequisite).ToArray();
                    var capacity = CoveragePolicy.PrerequisiteRate(plan, prerequisiteRows);
                    if (capacity <= 0)
                    {
                        await RecordAsync(
                            CaseResult.Incomplete(
                                descriptor.Id,
                                environment.Provider,
                                descriptor.Id == "E01" ? "soak" : "fault",
                                $"No independently validated {prerequisite} capacity is available for this declared workload."
                            )
                        );
                    }
                    else
                    {
                        var fallback =
                            CoveragePolicy.ValidatedCapacity(plan, prerequisiteRows) > 0
                                ? null
                                : $"Ran at {prerequisite}'s best qualifying discovery rate of {capacity} roots/s: no {prerequisite} rate validated, and the {plan.Profile} profile does not gate its verdict on measurement.";
                        var fraction = descriptor.Id == "E01" ? plan.Policy("soak", "offered_rate_fraction_of_validated_capacity") : 1;
                        for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                        {
                            var result = await TrialRunner.RunAsync(
                                plan,
                                descriptor,
                                environment,
                                bundle,
                                descriptor.Id == "E01" ? "soak" : "fault",
                                repetition,
                                Math.Max(1, (int)(capacity * fraction)),
                                ct,
                                overloadRate: descriptor.Id == "E03" ? (int)CoveragePolicy.OverloadRate(plan, prerequisiteRows) : 0
                            );
                            await RecordAsync(
                                fallback is null
                                    ? result
                                    : result with
                                    {
                                        Reason = result.Reason is null ? fallback : $"{fallback} {result.Reason}",
                                    }
                            );
                        }
                    }
                    continue;
                }
                if (descriptor.Id == "F09")
                {
                    await RecordAsync(await ChaosCase.RunAsync(plan, environment, bundle, ct));
                    continue;
                }
                if (descriptor.Id == "I03")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await OutboxCase.RunAsync(plan, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Family == "storage")
                {
                    await RecordAsync(await StorageCases.RunAsync(plan, descriptor, environment, bundle, ct));
                    if (descriptor.Id is not ("S02" or "S03"))
                    {
                        continue;
                    }
                }
                if (descriptor.Id is "R03" or "F08")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await TimeoutAndWakeupCases.RunAsync(plan, descriptor, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Id == "F04")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await ConnectivityCase.RunAsync(plan, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Id == "F03")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await StaleAttemptCase.RunAsync(plan, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Id is "P05" or "T03" or "T05")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await RaceCases.RunAsync(plan, descriptor, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Family == "ordering")
                {
                    await RecordAsync(await SchedulingCases.RunAsync(plan, descriptor, environment, bundle, ct));
                    if (descriptor.Id is "O04" or "O05")
                    {
                        continue;
                    }
                }
                if (descriptor.Id is "I01" or "I02")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await TransactionCases.RunAsync(plan, descriptor, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Id is "D02" or "D03" or "D04")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await RegistrationCases.RunAsync(plan, descriptor, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (descriptor.Id is "F06" or "F07")
                {
                    for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                    {
                        await RecordAsync(await ReplyLossCases.RunAsync(plan, descriptor, environment, bundle, ct, repetition));
                    }
                    continue;
                }
                if (FaultCaseRunner.Handles(descriptor.Id))
                {
                    if (environment.Provider == "sqlite" && descriptor.Id == "F05")
                    {
                        await RecordAsync(
                            new(
                                descriptor.Id,
                                environment.Provider,
                                "fault",
                                0,
                                "NOT_APPLICABLE",
                                "SQLite is embedded; the profile has no separate database process.",
                                "NOT_APPLICABLE",
                                "NOT_APPLICABLE",
                                "NOT_APPLICABLE",
                                "NOT_MEASURED",
                                [],
                                ["resolved-plan.json"]
                            )
                        );
                    }
                    else
                    {
                        for (var repetition = 1; repetition <= plan.TargetedFaultRepetitions; repetition++)
                        {
                            await RecordAsync(await FaultCaseRunner.RunAsync(plan, descriptor, environment, bundle, repetition, ct));
                        }
                    }
                    if (descriptor.Id != "I05")
                    {
                        continue;
                    }
                }
                if (!Implemented.Contains(descriptor.Id) && descriptor.Family != "ordering")
                {
                    await RecordAsync(
                        CaseResult.Incomplete(
                            descriptor.Id,
                            environment.Provider,
                            descriptor.Phases[0],
                            "Required case execution is not implemented yet."
                        )
                    );
                    continue;
                }
                for (var repetition = 1; repetition <= BoundaryRepetitions(plan, descriptor); repetition++)
                {
                    await RecordAsync(await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "verify", repetition, 0, ct));
                }
                if (!descriptor.Phases.Contains("measure", StringComparer.Ordinal) || !CoveragePolicy.IsCanonical(plan, descriptor.Id))
                {
                    continue;
                }
                if (descriptor.Id == "L04")
                {
                    for (var repetition = 1; repetition <= plan.Repetitions; repetition++)
                    {
                        await RecordAsync(await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "measure", repetition, 0, ct));
                    }
                    await RepeatVariableCellAsync(descriptor, 0, null);
                    foreach (var cell in CoveragePolicy.RequiredCells(plan, environment.Provider, descriptor.Id))
                    {
                        for (var repetition = 1; repetition <= plan.Repetitions; repetition++)
                        {
                            await RecordAsync(
                                await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "measure", repetition, 0, ct, cell)
                            );
                        }
                        await RepeatVariableCellAsync(descriptor, 0, cell);
                    }
                    continue;
                }
                // Discovery is explicitly separate evidence, followed by fresh measured repetitions.
                var rate = (int)plan.Policy("capacity", "discovery_start_rate");
                var best = 0;
                var discoveryLimit = plan.ProfilePolicy("discovery_attempt_limit");
                var discoveryStop = "Reached the predeclared discovery attempt limit; capacity is a highest observed lower bound.";
                var harnessBound = false;
                for (var attempt = 0; attempt < discoveryLimit; attempt++)
                {
                    if (!TrialRunner.CohortFitsBudget(descriptor.Id, (long)rate * plan.MeasurementSeconds))
                    {
                        discoveryStop =
                            "The next measured cohort would exceed the declared controller model memory budget; no larger rate was attempted.";
                        break;
                    }
                    var discovery = await RecordAsync(
                        await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "discover", attempt, rate, ct)
                    );
                    if (!CoveragePolicy.IsDiscoveryCandidate(discovery))
                    {
                        if (best == 0 && rate > 1)
                        {
                            rate = Math.Max(1, rate / 2);
                            continue;
                        }
                        harnessBound = discovery.Measurement == "GENERATOR_LIMITED";
                        discoveryStop = harnessBound
                            ? "The harness could not offer the next discovery rate; the highest qualifying rate is a harness-bound lower bound, not a capacity."
                            : "The next discovery point did not qualify; all trials are retained.";
                        break;
                    }
                    best = rate;
                    if (plan.Profile == "quick")
                    {
                        discoveryStop = "Quick mode stops at the first qualifying discovery point; it does not locate maximum capacity.";
                        break;
                    }
                    rate *= 2;
                }
                if (best == 0)
                {
                    await RecordAsync(
                        CaseResult.MeasurementGap(
                            plan,
                            descriptor.Id,
                            environment.Provider,
                            "measure",
                            "Discovery did not establish a valid rate; no capacity score is available."
                        )
                    );
                    continue;
                }
                var selectedRates = CoveragePolicy.SelectedRates(plan, completed.Where(r => r.Id == descriptor.Id));
                var fallbackRates = CoveragePolicy.FallbackRates(plan, completed.Where(r => r.Id == descriptor.Id));
                bundle.Write(
                    $"histories/{environment.Provider}-{descriptor.Id}-selected-rates.json",
                    new Dictionary<string, string>
                    {
                        ["offered_rates"] = string.Join(",", selectedRates),
                        ["fallback_rates"] = string.Join(",", fallbackRates),
                        ["frozen_before_measured_trials"] = "true",
                        ["discovery_stop_reason"] = discoveryStop,
                        ["harness_bound"] = harnessBound ? "true" : "false",
                    },
                    VerifyJson.Default.DictionaryStringString
                );
                // Rotate the frozen rates between repetitions to avoid a fixed low-to-high order.
                for (var repetition = 1; repetition <= plan.Repetitions; repetition++)
                {
                    for (var index = 0; index < selectedRates.Length; index++)
                    {
                        var selectedRate = (int)selectedRates[(index + repetition - 1) % selectedRates.Length];
                        await RecordAsync(
                            await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "measure", repetition, selectedRate, ct)
                        );
                    }
                }
                foreach (var selectedRate in selectedRates)
                {
                    await RepeatVariableCellAsync(descriptor, (int)selectedRate, null);
                }
                foreach (var fallbackRate in fallbackRates)
                {
                    if (CoveragePolicy.ValidatedCapacity(plan, completed.Where(r => r.Id == descriptor.Id)) > 0)
                        break;
                    for (var repetition = 1; repetition <= plan.Repetitions; repetition++)
                    {
                        await RecordAsync(
                            await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "measure", repetition, (int)fallbackRate, ct)
                        );
                    }
                    await RepeatVariableCellAsync(descriptor, (int)fallbackRate, null);
                }
                foreach (var cell in CoveragePolicy.RequiredCells(plan, environment.Provider, descriptor.Id))
                {
                    for (var repetition = 1; repetition <= plan.Repetitions; repetition++)
                    {
                        await RecordAsync(
                            await TrialRunner.RunAsync(
                                plan,
                                descriptor,
                                environment,
                                bundle,
                                "measure",
                                repetition,
                                (int)selectedRates[0],
                                ct,
                                cell
                            )
                        );
                    }
                    await RepeatVariableCellAsync(descriptor, (int)selectedRates[0], cell);
                }
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
                var reason = $"Case did not finish ({ex.GetType().Name}: {ex.Message}); partial histories retained.";
                Console.WriteLine($"  {descriptor.Id} {environment.Provider}: {reason}");
                File.WriteAllText(bundle.Resolve($"logs/{environment.Provider}-{descriptor.Id}-error.txt"), ex.ToString());
                await RecordAsync(CaseResult.Incomplete(descriptor.Id, environment.Provider, "verify", reason));
            }
        }

        async Task<CaseResult> RecordAsync(CaseResult result)
        {
            result = await LedgerValidation.ApplyAsync(result, environment.TakeTrials(), bundle, ct);
            // Results are recorded one after another, so the time since the previous one is the time this one took.
            var recorded = System.Diagnostics.Stopwatch.GetTimestamp();
            result.Metrics["wall_seconds"] = new(
                (recorded - lastRecorded) / (double)System.Diagnostics.Stopwatch.Frequency,
                "seconds",
                Reason: "Time from the previous recorded result to this one, its setup, trial and ledger checks included."
            );
            lastRecorded = recorded;
            if (
                result is { Id: "L04", Phase: "measure", DiagnosticCellName: null, Status: "PASS" }
                && result.Metrics.GetValueOrDefault("backlog_drain_rate")?.Value is { } drain
            )
            {
                environment.DrainRate = Math.Min(environment.DrainRate ?? drain, drain);
            }
            completed.Add(result);
            emit(result);
            return result;
        }

        async Task RepeatVariableCellAsync(ScenarioDescriptor descriptor, int rate, TrialCell? cell)
        {
            if (!plan.IsExtended)
                return;
            var initial = completed
                .Where(r =>
                    r.Id == descriptor.Id
                    && r.Phase == "measure"
                    && r.DiagnosticCellName == cell?.Name
                    && r.Repetition <= plan.Repetitions
                    && (r.Metrics.GetValueOrDefault("offered_rate")?.Value ?? 0) == rate
                )
                .ToArray();
            if (!PerformanceSummary.IsVariable(plan, initial))
                return;
            for (var repetition = plan.Repetitions + 1; repetition <= plan.Repetitions + plan.AdditionalRepetitions; repetition++)
            {
                await RecordAsync(await TrialRunner.RunAsync(plan, descriptor, environment, bundle, "measure", repetition, rate, ct, cell));
            }
        }
    }

    private static int BoundaryRepetitions(RunPlan plan, ScenarioDescriptor scenario) =>
        scenario.Phases.Contains("fault", StringComparer.Ordinal) ? plan.TargetedFaultRepetitions : 1;
}
