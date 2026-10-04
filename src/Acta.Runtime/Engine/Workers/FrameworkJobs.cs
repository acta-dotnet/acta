namespace Acta.Runtime;

/// <summary>
/// Which jobs of the generated <c>RuntimeJobs</c> framework manifest a worker namespace registers.
/// <see cref="JobsOptions.RegisterSystemJobs"/> brings the maintenance set; an outbox relay brings <c>sys.outbox</c>,
/// which is opt-in and registers only through <c>AddOutboxRelay</c>. Either one needs <c>sys.recovery</c> and
/// <c>sys.alerts</c>, so a relay registers them even when automatic registration is off, and never forces the
/// unrelated <c>sys.retention</c>.
/// </summary>
internal static class FrameworkJobs
{
    public static IReadOnlySet<string> For(bool registerSystemJobs, bool hasRelay)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (registerSystemJobs || hasRelay)
        {
            names.Add("sys.recovery");
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
}
