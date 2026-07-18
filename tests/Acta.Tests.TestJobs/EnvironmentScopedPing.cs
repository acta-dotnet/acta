using Acta;

namespace TestJobs;

/// <summary>
/// Probes environment-gated schedule registration. <c>env-gated-ping</c> declares one schedule scoped
/// to <c>staging</c> and one scoped to <c>production</c>, so a worker running as either environment
/// registers exactly one of them. <c>env-prod-only-ping</c> declares a single production-scoped
/// schedule, so a non-production worker registers no slot for it at all. Both are zero-input handlers
/// (the schedule never reads a payload), which keeps them exempt from the duplicate-input-type route
/// warning (ACTA0104) without needing a marker request type each.
/// </summary>
public static class EnvironmentScopedPingHandler
{
    [Job("env-gated-ping")]
    [JobSchedule("staging-tick", Cron.Every5Minutes, Environments = new[] { "staging" })]
    [JobSchedule("prod-tick", Cron.Every5Minutes, Environments = new[] { "production" })]
    public static Task Run() => Task.CompletedTask;

    [Job("env-prod-only-ping")]
    [JobSchedule("prod-tick", Cron.Every5Minutes, Environments = new[] { "production" })]
    public static Task RunProdOnly() => Task.CompletedTask;
}
