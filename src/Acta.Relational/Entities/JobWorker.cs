using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Worker registration and liveness row. One row per worker process; every
/// <c>WorkerRuntime.InitializeAsync</c> call INSERTs a fresh row representing that runtime's
/// lifetime, identified by the DB-assigned <see cref="Id"/>. A single host can produce many rows
/// (multiple containers, multi-runtime processes, restarts), and rows are never reused. The
/// retention sweep deletes <c>Dead</c> workers older than <c>JobsOptions.WorkerRetention</c>
/// (default <c>P90D</c>).
/// </summary>
[DbTable("workers")]
[DbPrimaryKey(Name = "pk_workers", Columns = ["id"])]
[DbIndex(Name = "ix_workers_namespace_status_lastseen", Columns = ["namespace_id", "status_code", "last_seen_at_utc"], Usage = "dashboard")]
[DbIndex(Name = "ix_workers_status_lastseen", Columns = ["status_code", "last_seen_at_utc"], Usage = "maintenance")]
[DbIndex(
    Name = "ix_workers_namespace_last_seen",
    Columns = ["namespace_id", "last_seen_at_utc", "id"],
    Descending = ["last_seen_at_utc", "id"],
    Usage = "dashboard_grid"
)]
[DbForeignKey(
    Name = "fk_workers_namespaces",
    Target = typeof(JobNamespace),
    TargetColumn = "id",
    Column = "namespace_id",
    OnDelete = DbForeignKeyAction.NoAction
)]
[DbCheck(Name = "ck_workers_max_concurrency", Sql = "max_concurrency > 0")]
internal sealed class JobWorker : IEntity<int>
{
    /// <summary>
    /// Worker process identifier; never reused.
    /// </summary>
    [DbColumn("id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Namespace this worker claims within. Enforced by <c>fk_workers_namespaces</c>.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Worker lifecycle status, written by the <c>StartWorker</c> handler at startup. <c>sys.recovery</c>
    /// flips stale rows to <c>Dead</c> when <see cref="LastSeenAtUtc"/> falls past
    /// <c>JobsOptions.WorkerDeadAfter</c>, alongside its stuck-job lease reclaim.
    /// </summary>
    [DbColumn("status_code")]
    public WorkerStatusCode Status { get; set; }

    /// <summary>
    /// Opaque deployment version (git SHA, build number) for triage.
    /// </summary>
    [DbColumn("deployment_version", DbKind.AsciiString, Size = 128)]
    public string DeploymentVersion { get; init; } = default!;

    /// <summary>
    /// Hostname or pod name; informational.
    /// </summary>
    [DbColumn("host", DbKind.AsciiString, Size = 256)]
    public string Host { get; init; } = default!;

    /// <summary>
    /// Acta engine assembly informational version, distinct from <see cref="DeploymentVersion"/> (the
    /// host app's version). Stamped once at registration. NULL when the Acta engine informational
    /// version could not be resolved at registration.
    /// </summary>
    [DbColumn("engine_version", DbKind.AsciiString, Size = 128)]
    public string? EngineVersion { get; init; }

    /// <summary>
    /// Runtime framework description, e.g. ".NET 10.0.0" (<c>RuntimeInformation.FrameworkDescription</c>).
    /// NULL when <c>RuntimeInformation.FrameworkDescription</c> was unavailable at registration.
    /// </summary>
    [DbColumn("dotnet_version", DbKind.AsciiString, Size = 64)]
    public string? DotnetVersion { get; init; }

    /// <summary>
    /// OS process id of the worker process (<c>Environment.ProcessId</c>); informational. NULL when the
    /// OS process id was not captured / unavailable.
    /// </summary>
    [DbColumn("process_id", DbKind.Int32)]
    public int? ProcessId { get; init; }

    /// <summary>
    /// Effective per-process executor cap (<c>JobsOptions.MaxConcurrentExecutors</c>) recorded at
    /// registration.
    /// </summary>
    [DbColumn("max_concurrency", DbKind.Int32)]
    public int MaxConcurrency { get; init; }

    /// <summary>
    /// Liveness heartbeat. Written on startup by <c>StartWorker</c> and refreshed every
    /// <c>JobsOptions.HeartbeatInterval</c> by <c>WorkerHeartbeat</c> while the worker runs.
    /// </summary>
    [DbColumn("last_seen_at_utc", DbKind.UtcInstant)]
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>
    /// Process start time.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the worker row was last updated. Set server-side on every mutation.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token; SPs manually increment on UPDATE.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
