namespace Acta.Verify;

public static class HarnessControls
{
    /// <summary>The self-checks every run starts with, each a planted defect the checks must reject.</summary>
    public static readonly string[] Names =
    [
        "self-check-missing-job",
        "self-check-missing-checkpoint",
        "self-check-duplicate-job",
        "self-check-missing-child",
        "self-check-wrong-result",
        "self-check-unlanded-fault",
        "self-check-harness-lateness",
        "self-check-interrupted-run",
    ];

    public static CaseResult[] Run(RunPlan plan, EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bundle);
        var missedFraction = plan.Policy("capacity", "max_generator_missed_fraction_for_valid_capacity");
        ExpectedOutcome[] expected = [new("root", "Succeeded", "42", Steps: ["one=1", "two=2"]), new("child", "Succeeded", "40", "root")];
        ObservedOutcome[] valid = [new("root", "Succeeded", "42", Steps: ["one=1", "two=2"]), new("child", "Succeeded", "40", "root")];
        if (OutcomeCheck.Reconcile(expected, valid).Any(c => !c.Passed))
        {
            throw new InvalidOperationException("The outcome check rejects the valid control fixture.");
        }
        var results = new List<CaseResult>();
        Test("self-check-missing-job", [valid[1]], "missing-identity");
        Test("self-check-missing-checkpoint", [valid[0] with { Steps = ["one=1"] }, valid[1]], "committed-steps");
        Test("self-check-duplicate-job", [.. valid, valid[0]], "unique-terminal");
        Test("self-check-missing-child", [valid[0], valid[1] with { Key = "substitute" }], "missing-identity");
        Test("self-check-wrong-result", [valid[0] with { Output = "43" }, valid[1]], "logical-result");
        Record("self-check-unlanded-fault", !OutcomeCheck.FaultCovered(new("busy-kill", [], false, true, false), true));
        Record(
            "self-check-harness-lateness",
            Verdicts.Measurement(1000, 10, false, 1, 1000, 0, 0.1, missedFraction) == "GENERATOR_LIMITED"
                && Verdicts.Measurement(1000, 0, true, 1, 1000, 0, 0.1, missedFraction) == "OBSERVER_LIMITED"
        );
        Record("self-check-interrupted-run", Verdicts.Overall([], true, true) == "ABORTED");
        return [.. results];

        void Test(string id, ObservedOutcome[] corrupted, string rejectedBy)
        {
            var checks = OutcomeCheck.Reconcile(expected, corrupted);
            bundle.Write($"histories/{id}-expected.json", expected, VerifyJson.Default.ExpectedOutcomeArray);
            bundle.Write($"histories/{id}-observed.json", corrupted, VerifyJson.Default.ObservedOutcomeArray);
            bundle.Write($"histories/{id}-checks.json", checks, VerifyJson.Default.CheckArray);
            Record(id, checks.Any(c => c.Code == rejectedBy && !c.Passed));
        }

        void Record(string id, bool passed)
        {
            var checks = new[] { new Check("negative-control", id, passed, "Synthetic harness evidence; not an engine defect.") };
            bundle.Write($"histories/{id}-control.json", checks, VerifyJson.Default.CheckArray);
            results.Add(
                new(
                    id,
                    "harness",
                    "self_test",
                    0,
                    passed ? "PASS" : "INCONCLUSIVE",
                    passed
                        ? "Synthetic harness evidence; not an engine case."
                        : "Harness self-test failed, so the harness cannot judge engine evidence; not an engine defect.",
                    passed ? "PASS" : "UNVERIFIED",
                    "NOT_APPLICABLE",
                    "COMPLETE",
                    "NOT_MEASURED",
                    [],
                    [$"histories/{id}-control.json"]
                )
            );
        }
    }
}
