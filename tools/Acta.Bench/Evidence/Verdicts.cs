namespace Acta.Bench;

public static class Verdicts
{
    public static string Overall(IEnumerable<CaseResult> results, bool aborted, bool settingsVerified)
    {
        var cases = results.ToArray();
        if (cases.Any(c => c.Status == "FAIL"))
        {
            return "FAIL";
        }
        if (aborted)
        {
            return "ABORTED";
        }
        return settingsVerified && cases.Length > 0 && cases.All(c => c.Status is "PASS" or "NOT_APPLICABLE") ? "PASS" : "INCONCLUSIVE";
    }

    /// <summary><paramref name="reason"/>, marked as reported only because the plan's profile does not gate its verdict on measurement.</summary>
    public static string Informational(RunPlan plan, string reason) =>
        $"{reason} Informational: capacity measurement does not decide the {plan.Profile} profile's verdict.";

    public static int ExitCode(string status, bool harnessError = false) =>
        status switch
        {
            "FAIL" => 1,
            "ABORTED" => 130,
            _ when harnessError => 3,
            "PASS" => 0,
            _ => 2,
        };

    public static string Measurement(
        long intended,
        long missed,
        bool observerLost,
        double p99Ms,
        double sloMs,
        double backlogSlope,
        double slopeTolerance,
        double maxMissedFraction
    ) =>
        observerLost ? "OBSERVER_LIMITED"
        : intended == 0 || missed / (double)intended > maxMissedFraction ? "GENERATOR_LIMITED"
        : p99Ms > sloMs || backlogSlope > slopeTolerance ? "UNSTABLE"
        : "VALID";

    public static bool InWindow(long observed, long start, long end) => observed >= start && observed < end;
}
