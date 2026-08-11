namespace Anvil;

/// <summary>
/// Per-process lab identity: the run id, the boot instant, and batch sequencing. Hands out a fresh batch
/// number per seed so each click enqueues new work instead of deduping against the previous batch.
/// </summary>
public sealed record AnvilSession(string RunId, string NamespaceName, string Schema, string Provider, DateTime ProcessStartUtc)
{
    private int _batch;
    private long _expectedFailures;

    /// <summary>
    /// Non-null while this process is running a certification. It is the single answer to "is a
    /// certification in progress?" for the whole process: the endpoints refuse mutations while it is
    /// set, and the cockpit renders it as a banner. Without it the lab looked idle - the setup panel
    /// showed its own defaults (no-op, 2 workers) rather than the certification's shape, and every
    /// button was live, so one click could seed into or crash a run being sealed.
    /// </summary>
    public CertificationStatus? Certification { get; set; }

    /// <summary>Next monotonic batch number; each seed click gets its own so deduplication keys stay unique.</summary>
    public int NextBatch() => Interlocked.Increment(ref _batch);

    /// <summary>
    /// Adds the always-fails jobs a seed actually enqueued (zero for a NOOP run, whose load never fails),
    /// so the board's Failed target reflects what was seeded rather than a fixed per-batch assumption.
    /// </summary>
    public void AddExpectedFailures(long count) => Interlocked.Add(ref _expectedFailures, count);

    /// <summary>Total always-fails jobs seeded across this run so far; the Failed counter's denominator.</summary>
    public long ExpectedFailures => Interlocked.Read(ref _expectedFailures);
}

/// <summary>
/// The identity of one run: a run id plus the schema and namespace derived from it. A short random
/// suffix on the run id keeps two runs started in the same second from colliding on a namespace. The
/// dashboard defaults to one shared schema (<see cref="DefaultDashboardSchema"/>) in the local database
/// with a fresh per-run namespace, so repeated runs accumulate their namespaces into a single catalog the
/// operator dashboard can grow; <c>--schema</c> overrides it (e.g. a unique name for a throwaway run) and
/// <c>--namespace</c> pins the namespace. Schema names are valid SQL identifiers (underscores); namespaces
/// are kebab.
/// </summary>
public sealed record RunIdentity(string RunId, string Schema, string Namespace)
{
    /// <summary>The schema dashboard runs share by default so their namespaces accumulate in one place.</summary>
    public const string DefaultDashboardSchema = "anvil";

    public static RunIdentity NewDashboard(DateTime utcNow, string? schema = null, string? @namespace = null, string? suffix = null)
    {
        suffix ??= NewSuffix();
        return new RunIdentity(NewRunId(utcNow, suffix), schema ?? DefaultDashboardSchema, @namespace ?? NewNamespace(utcNow));
    }

    // The run id carries the random suffix so two runs started in the same wall-clock second (parallel
    // agents, a quick restart) cannot collide within the shared dashboard schema and intermix their
    // jobs/workers/events. The fixed-width timestamp leads, so name-ascending order stays chronological -
    // the newest run sorts last in the dashboard scope dropdown. The namespace has no suffix, so two runs
    // in the same second do collide there (accepted: local tool).
    private static string NewRunId(DateTime utcNow, string suffix) => $"r{utcNow:yyyyMMdd-HHmmss}-{suffix}";

    private static string NewNamespace(DateTime utcNow) => $"anvil-{utcNow:yyyyMMdd-HHmmss}";

    // 6 hex chars of uniqueness for the run id; a same-second collision is otherwise possible.
    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..6];
}

/// <summary>Demo tenants the dashboard registers at boot so tenant-scoped dashboard views have data.</summary>
public static class AnvilTenants
{
    public static readonly (string Key, string DisplayName)[] All =
    [
        ("acme", "Acme Corp"),
        ("globex", "Globex Corporation"),
        ("initech", "Initech"),
    ];
}

/// <summary>One certification's shape and current phase, as the cockpit shows it.</summary>
public sealed record CertificationStatus(string Phase, string Detail, int Jobs, int Workers, int ChaosMinutes);
