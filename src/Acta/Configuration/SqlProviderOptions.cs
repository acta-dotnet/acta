namespace Acta;

/// <summary>
/// Connection and schema options shared by every SQL provider package. Each provider exposes a
/// sealed subclass (e.g. <c>SqlServerProviderOptions</c>) so registration stays provider-typed.
/// </summary>
public abstract class SqlProviderOptions
{
    /// <summary>Safety ceiling for deadlock retry attempts.</summary>
    public const int MaxDeadlockRetryAttempts = 100;

    /// <summary>
    /// Provider connection string. Master and target DB negotiated at migration time.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Schema name in which Acta tables, indexes, views, and sequences live. Default <c>acta</c>;
    /// override pre-install for co-deployment-per-DB scenarios.
    /// </summary>
    public string Schema { get; set; } = "acta";

    /// <summary>
    /// Maximum execution time for every runtime operation command, applied by the store to all
    /// operations uniformly. Rounded up to whole seconds. Default 30 seconds. Must be positive;
    /// startup validation rejects zero (ADO.NET's infinite-timeout sentinel), which conflicts with
    /// the lease model.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum attempts for a store operation the database aborted as a deadlock victim, counting the
    /// first try. The rolled-back operation re-runs on a fresh connection with a small randomized
    /// backoff between attempts. Default 5; set to 1 to disable retry. Startup validation rejects a
    /// value outside 1..<see cref="MaxDeadlockRetryAttempts"/>.
    /// </summary>
    public int DeadlockRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Dev and sample convenience: when <c>true</c>, the runtime's <c>InitializeAsync</c> creates
    /// the application database if it's missing and applies pending migrations and installs the
    /// routines before any catalog writes happen. Default <c>false</c>; production deployments
    /// should keep DB lifecycle in infrastructure (operator scripts, IaC) and treat the schema
    /// as a precondition of the runtime, not a side effect of it.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = false;

    /// <summary>
    /// What provider bootstrap does when the loaded ADO driver assembly's major version differs from
    /// the major this provider package was certified against. Default
    /// <see cref="DriverVersionPolicy.Fail"/>: the check runs before any SQL, in either direction,
    /// and is the only real lock on the driver version because the package dependency is an
    /// unbounded floor. Set to <see cref="DriverVersionPolicy.Warn"/> to run on an uncertified major
    /// and accept the one structured warning that says so.
    /// </summary>
    public DriverVersionPolicy DriverVersionPolicy { get; set; } = DriverVersionPolicy.Fail;
}
