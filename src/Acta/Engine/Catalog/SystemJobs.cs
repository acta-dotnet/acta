namespace Acta;

/// <summary>
/// Which jobs of the generated <c>ActaJobs</c> framework manifest a worker namespace registers, and whether its worker
/// runs crash recovery. <see cref="JobsOptions.RegisterSystemJobs"/> brings the maintenance set; an outbox relay brings
/// <c>sys.outbox</c>, which is opt-in and registers only through <c>AddOutboxRelay</c>. Either one needs crash recovery
/// and <c>sys.alerts</c>, so a relay brings them even when automatic registration is off, and never forces the
/// unrelated <c>sys.retention</c>.
/// </summary>
internal static class SystemJobs
{
    public static IReadOnlySet<string> For(bool registerSystemJobs, bool hasRelay)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (registerSystemJobs || hasRelay)
        {
            names.Add("sys.alerts");
        }

        if (registerSystemJobs)
        {
            names.Add("sys.retention");
        }

        if (hasRelay)
        {
            names.Add("sys.outbox");
        }

        return names;
    }

    /// <summary>Whether the worker's heartbeat takes crash recovery passes, under the same rule as <c>sys.alerts</c>.</summary>
    public static bool RunsRecovery(bool registerSystemJobs, bool hasRelay) => registerSystemJobs || hasRelay;
}
