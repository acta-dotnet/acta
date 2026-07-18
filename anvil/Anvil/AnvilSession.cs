namespace Anvil;

/// <summary>
/// Per-process lab identity: the run id, the boot instant, and batch sequencing. Hands out a fresh batch
/// number per seed so each click enqueues new work instead of deduping against the previous batch.
/// </summary>
public sealed record AnvilSession(string RunId, string NamespaceName, string Schema, string Provider, DateTime ProcessStartUtc)
{
    private int _batch;
    private long _expectedFailures;

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
        return new RunIdentity(NewRunId(utcNow, suffix), schema ?? DefaultDashboardSchema, @namespace ?? NewNamespace(utcNow, suffix));
    }

    // Both the run id and the namespace carry the random suffix so two runs started in the same wall-clock
    // second (parallel agents, a quick restart) cannot collide on one namespace within the shared dashboard
    // schema and intermix their jobs/workers/events. The fixed-width timestamp leads in both, so
    // name-ascending order stays chronological - the newest run sorts last in the dashboard scope dropdown.
    private static string NewRunId(DateTime utcNow, string suffix) => $"r{utcNow:yyyyMMdd-HHmmss}-{suffix}";

    private static string NewNamespace(DateTime utcNow, string suffix) => $"anvil-{utcNow:yyyyMMdd-HHmmss}-{suffix}";

    // 6 hex chars of uniqueness for the run id; a same-second collision is otherwise possible.
    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..6];
}
