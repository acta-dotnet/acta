using Acta;

namespace TestJobs;

public sealed record PolicyProbe(string Note);

public static class PolicyProbeHandler
{
    // Carries the full per-definition policy surface so registration wiring (attribute -> descriptor
    // -> definitions columns) is exercised end to end.
    [Job(
        "policy-probe",
        MaxAttempts = 7,
        Priority = JobPriorityCode.High,
        Backoff = "30s..2h x3 ±25%",
        ExecutionTimeout = "45s",
        JobRetention = "7d",
        AuditLevel = JobAuditLevelCode.Off,
        AlertChannelName = "ops",
        RunbookUrl = "https://runbook.example/policy-probe",
        DisplayName = "Policy Probe",
        Description = "Probes full attribute policy persistence."
    )]
    public static void Run(PolicyProbe input) { }
}
