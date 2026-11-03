namespace Acta;

/// <summary>Connection and schema options shared by every SQL provider package.</summary>
public sealed class SqlProviderOptions
{
    /// <summary>Safety ceiling for deadlock retry attempts.</summary>
    public const int MaxDeadlockRetryAttempts = 100;

    /// <summary>
    /// Provider connection string, naming the database that holds the Acta schema.
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
    /// Development only: when <c>true</c>, startup creates the database if it is missing and applies the migrations it
    /// lacks before any catalog write. Default <c>false</c>: production applies them from a deploy step, with
    /// <c>&lt;app&gt; jobs migrate</c> or the published script, and treats the schema as a precondition of the runtime.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = false;
}
