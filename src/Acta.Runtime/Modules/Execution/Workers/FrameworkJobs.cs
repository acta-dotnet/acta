namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Partitions the generated <c>RuntimeJobs</c> framework manifest into the automatic maintenance set and
/// the relay set. <c>sys.outbox</c> is opt-in: it registers only when a worker calls
/// <c>AddOutboxRelay</c>, never through <see cref="JobsOptions.RegisterFrameworkJobs"/>. Registering a
/// relay pulls in <c>sys.outbox</c> plus its <c>sys.recovery</c> and <c>sys.alerts</c> dependencies
/// even when automatic registration is off, and never forces the unrelated <c>sys.retention</c> job.
/// </summary>
internal static class FrameworkJobs
{
    /// <summary>Auto-registered per namespace when <see cref="JobsOptions.RegisterFrameworkJobs"/> is on.</summary>
    public static readonly IReadOnlySet<string> AutomaticNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "sys.alerts",
        "sys.recovery",
        "sys.retention",
    };

    /// <summary>Added when a relay source is registered on the worker.</summary>
    public static readonly IReadOnlySet<string> RelayNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "sys.outbox",
        "sys.recovery",
        "sys.alerts",
    };
}
