using Acta;

namespace TestJobs;

public sealed record PurgeProbe(string Note);

public sealed record PurgeProbeResult(string Note);

public static class PurgeProbeHandler
{
    // Zero retention: complete_executions stamps retention_until_utc = completion-time now, so the
    // next sys.retention sweep (a strictly later now) deletes this terminal job and CASCADEs
    // its results child. Used by the retention-purge conformance spec to produce a deterministically
    // deletable terminal row through the real enqueue/execute/complete path.
    [Job("purge-now", JobRetention = "0s")]
    public static PurgeProbeResult Run(PurgeProbe input) => new(input.Note);

    // The longest retention there is; one attempt, so a lapsed lease fails the job at its first reclaim.
    [Job("keep-forever", JobRetention = "never", MaxAttempts = 1)]
    public static void KeepForever() { }
}
