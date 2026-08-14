namespace Acta;

/// <summary>
/// Tunable settings for an Acta deployment, bound via <c>IOptions&lt;JobsOptions&gt;</c>.
/// Per-definition policy (retry, backoff, retention, alert profile) lives on <c>JobDefinition</c> and
/// <c>[Job(...)]</c>, not here.
/// </summary>
/// <remarks>
/// The coordination triple - heartbeat, lease window, dead-worker window - must agree across every
/// worker or the reclaim math desyncs, and nothing verifies that at runtime. So only
/// <see cref="HeartbeatInterval"/> is settable and the other two derive from it, holding the ratio by
/// construction. Engine tuning with no operator-legible meaning is not exposed at all. Per-process
/// settings are safe to differ; <see cref="DeploymentVersion"/> must differ across a rolling deploy.
/// </remarks>
public sealed class JobsOptions
{
    /// <summary>
    /// Retention window in days for every <c>JobEvent</c> row, audit timeline and execution ledger
    /// alike.
    /// </summary>
    public TimeSpan JobEventsRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Retention window in days for settled <c>JobAlert</c> rows (Suppressed, Delivered, or Failed);
    /// in-flight deliveries are never purged regardless of age. Default 90 days, shorter than
    /// <see cref="JobEventsRetention"/> because an alert is a projection of the <c>events</c>
    /// ledger, which keeps the incident timeline for the full event window.
    /// </summary>
    public TimeSpan AlertRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// How long Dead <c>JobWorker</c> rows are retained past <c>LastHeartbeatAtUtc</c> before
    /// <c>sys.retention</c> sweeps them. Default 90 days.
    /// </summary>
    public TimeSpan WorkerRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Per-process maximum number of concurrent in-flight executions per worker runtime. Default
    /// <c>clamp(ProcessorCount * 4, 8, 64)</c>: IO-bound oversubscription with a small-container floor
    /// and an ADO.NET-pool-friendly ceiling. Fixed at startup; not autoscaled.
    /// </summary>
    public int MaxConcurrentExecutors { get; set; } = Math.Clamp(Environment.ProcessorCount * 4, 8, 64);

    /// <summary>
    /// Per-process upper bound on how long an idle claim loop sleeps between polls. Immediate pickup
    /// is wakeup-driven through <see cref="IWorkerWakeup"/>; this interval is the correctness floor
    /// that bounds discovery of work made Ready by a process this one shares no wakeup transport with,
    /// and of any lost signal. Default 1s; raise it to trade recovery latency for fewer idle DB
    /// round-trips.
    /// </summary>
    public TimeSpan SafetyPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Lower clamp on the idle claim-loop sleep, so an empty claim whose horizon says work is already
    /// due (a due row transiently locked by another worker) retries after this floor instead of
    /// spinning. Fixed at 50ms: engine tuning with no operator-legible meaning.
    /// </summary>
    internal TimeSpan MinPollFloor { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Maximum random jitter added to the idle claim-loop sleep so workers holding the same deadline
    /// wake staggered instead of stampeding the claim index. A signaled wakeup returns unjittered, the
    /// sleep never extends past <see cref="SafetyPollInterval"/>, and a deadline-woken job may be
    /// claimed up to this much after its due instant. Fixed at 100ms.
    /// </summary>
    internal TimeSpan ClaimIdleJitterMax { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Per-process maximum number of Ready jobs the claim loop pulls per poll into the dispatch
    /// channel. Default 32, which amortises the per-poll round-trip under load; excess drains as
    /// executors free up. Must be positive; startup validation rejects a value below 1.
    /// </summary>
    public int ClaimBatchSize { get; set; } = 32;

    /// <summary>
    /// Fixed re-arm delay in seconds for a claimed exclusive-key job that finds its key lock held at
    /// execution admission. The loser returns to Ready with <c>next_run_at_utc</c> pushed this far
    /// forward (budget-neutral), so the delay is the contention throttle (no backoff, no counter).
    /// Fixed at 2s.
    /// </summary>
    internal int ExclusiveKeyBounceDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Coordination invariant, and the only one you set: cadence of the background loop that refreshes
    /// every in-flight lease this process holds and stamps <c>workers.last_seen_at_utc</c>. Runs on its
    /// own timer, so a flood of long-running handlers cannot starve it. Default 45s.
    /// <para>
    /// <see cref="LeaseTtlSeconds"/> and <see cref="WorkerDeadAfter"/> are derived from this rather than
    /// set beside it. All three must agree across every worker in a deployment or the reclaim math
    /// desyncs, and nothing can verify that agreement at runtime - so the ratios that matter are held by
    /// construction instead of by validation. Shorten this to make a crash-recovery demo watchable and
    /// the whole triple shortens with it, still correctly proportioned.
    /// </para>
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Derived, <see cref="HeartbeatInterval"/> x4: worker-wide lease window in seconds, set on every
    /// claim and refreshed by the heartbeat while a handler executes. The multiple is what keeps a live
    /// worker's own jobs from being reclaimed mid-run (double execution) - it can miss three consecutive
    /// beats before <c>sys.recovery</c> may take its leases. Long-running handlers stay alive through
    /// heartbeating rather than through a longer lease, which is why nothing here needs to grow for
    /// them. There is no per-definition override (that would re-add a JOIN to <c>definitions</c> on the
    /// hot claim path). Default 180s.
    /// </summary>
    public int LeaseTtlSeconds
    {
        get => _leaseTtlSeconds ?? (int)(HeartbeatInterval.TotalSeconds * LeaseHeartbeatMultiple);
        internal set => _leaseTtlSeconds = value;
    }

    /// <summary>
    /// Derived, <see cref="HeartbeatInterval"/> x7 - the lease window plus three further beats: how long
    /// a worker may go without heartbeating before <c>sys.recovery</c> flips its <c>workers</c> row from
    /// Active to Dead (measured against <c>last_seen_at_utc</c>). The margin past the lease is
    /// deliberate, so a worker whose leases just lapsed is not retired while it might still recover.
    /// <c>sys.recovery</c> applies the running worker's value, which is the second reason this is derived
    /// rather than set. The retention sweep deletes Dead rows once they exceed
    /// <see cref="WorkerRetention"/>. Default 315s.
    /// </summary>
    public TimeSpan WorkerDeadAfter
    {
        get => _workerDeadAfter ?? HeartbeatInterval * DeadAfterHeartbeatMultiple;
        internal set => _workerDeadAfter = value;
    }

    // The two ratios, named rather than inlined so the relationship is greppable from either derived
    // member. 4x is the lease's own margin (three missable beats); 7x is that plus three more before a
    // worker is tombstoned, because retiring one that might still recover is the expensive mistake.
    internal const int LeaseHeartbeatMultiple = 4;
    internal const int DeadAfterHeartbeatMultiple = 7;

    // The derived pair is decoupled only from inside the engine, and only by things that must vary it
    // independently: the benchmark sweeps the lease as an experiment variable, and the drain specs need
    // frequent beats beside a long lease so the suite's cross-test parallelism cannot starve a tick into
    // a spurious reclaim. Null means derived, which is every deployment.
    private int? _leaseTtlSeconds;
    private TimeSpan? _workerDeadAfter;

    /// <summary>
    /// Per-process deployment environment name this worker runs as, the value a <c>[JobSchedule]</c>'s
    /// <c>Environments</c> list is matched against (case-insensitively) to decide whether that schedule
    /// registers here. A schedule with no declared environments is a wildcard active everywhere; a
    /// scoped schedule registers only when its list contains this name, so a <c>"production"</c>-scoped
    /// schedule never registers on a worker running as anything else. Defaults to the standard .NET host
    /// environment (<c>DOTNET_ENVIRONMENT</c>, then <c>ASPNETCORE_ENVIRONMENT</c>, then
    /// <c>"Production"</c>), so an unconfigured host behaves like the framework host's own default; set it
    /// explicitly (e.g. <c>o.EnvironmentName = builder.Environment.EnvironmentName</c>) to bind it to your
    /// host. A null or empty value means no environment is known, so every scoped schedule is withheld
    /// and only wildcard schedules register.
    /// </summary>
    public string? EnvironmentName { get; set; } =
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    /// <summary>
    /// Per-process (may differ; the upsert is idempotent, so one registering worker suffices). When
    /// <c>true</c> (default), each worker namespace auto-registers the system jobs
    /// (<c>sys.alerts</c>, <c>sys.recovery</c>, <c>sys.retention</c>) and their recurring slots. Set
    /// <c>false</c> to opt out, for example when maintenance is driven externally or for tests that need
    /// a namespace free of the competing recurring slots.
    /// </summary>
    /// <remarks>
    /// <strong>Turning this off disables crash recovery:</strong> <c>sys.recovery</c> is the only thing
    /// that marks dead workers and reclaims their in-flight jobs, so a dead worker's jobs stay
    /// <c>Executing</c> behind a lapsed lease permanently. The runtime warns at startup when it is off.
    /// </remarks>
    // Named for what it governs - all three are sys. jobs - rather than "framework", which also implied
    // sys.outbox. It does not govern that one: sys.outbox registers only with an explicit
    // AddOutboxRelay, and that relay re-registers sys.recovery and sys.alerts as its dependencies even
    // when this is false. "Driven externally" means you have written the reclaim sweep yourself;
    // nothing in the box does it.
    public bool RegisterSystemJobs { get; set; } = true;

    /// <summary>
    /// The one payload ceiling, 1 MiB by default. It governs both what may be stored inline
    /// (<c>Job.Input</c>, <c>JobResult.Result</c>, <c>JobCheckpoint.Value</c>, <c>JobStep.Result</c>)
    /// and the largest request body the HTTP endpoints accept, so a body that would be refused by the
    /// ledger is refused at the edge with the same number rather than a second one.
    /// <para>
    /// Caller-controlled writes (enqueue, variables, progress, signals, step results) throw
    /// <c>PayloadTooLargeException</c> past it. A handler result past it is dropped rather than
    /// persisted: the job still succeeds and the events carry <c>job.result-oversized</c>.
    /// </para>
    /// <para>
    /// An HTTP body is a JSON envelope around the payload it carries, so a payload of exactly this
    /// size will not fit in a request of this size. That is deliberate: one number, applied to the
    /// thing the caller can actually measure.
    /// </para>
    /// </summary>
    public int MaxInlinePayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Width of the dedupe bucket for alerts raised with a non-null deduplication key (<c>ctx.AlertAsync</c> and
    /// the framework automatic-alert paths). Repeats sharing a <c>(namespace_id, deduplication_key)</c>
    /// that fall in the same window collapse onto one <c>alerts</c> row (incrementing
    /// <c>occurrence_count</c>); the window start is the caller's <c>now</c> floored to a multiple of
    /// this span. The dedupe window is the rate limit. Fixed at 1 hour.
    /// </summary>
    internal TimeSpan AlertDedupeWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many failed delivery attempts the <c>sys.alerts</c> deliver phase makes before a <c>alerts</c>
    /// is marked terminal <c>Failed</c>. Each retryable failure bumps <c>retry_count</c> and defers the
    /// next attempt by a backoff curve; once <c>retry_count</c> reaches this cap the row stops retrying.
    /// Fixed at 5.
    /// </summary>
    internal int AlertDeliveryMaxRetries { get; set; } = 5;

    /// <summary>
    /// Number of failures within the <see cref="AlertDedupeWindow"/> at which an automatic failure alert
    /// escalates to <c>ThresholdReached</c> (<c>Error</c> severity). The <c>sys.alerts</c> generate phase
    /// reads the post-upsert <c>occurrence_count</c> from <c>raise_job_alert</c> and escalates when it
    /// meets this value; reset-immune, with no JOIN to the mutable <c>runtimes.failure_count</c>. Default 3.
    /// </summary>
    public int AlertFailureThreshold { get; set; } = 3;

    /// <summary>
    /// Startup check that every alerting definition routes to a configured alert channel in its worker
    /// namespace (its declared <c>AlertChannelName</c>, else the implicit <c>"default"</c>). <c>Off</c>
    /// skips it, <c>Warn</c> (default) logs each unroutable definition, <c>Fail</c> throws at worker
    /// initialization.
    /// </summary>
    public AlertChannelValidationMode AlertChannelValidationMode { get; set; } = AlertChannelValidationMode.Warn;

    /// <summary>
    /// Worker behavior when an eligible registration changes a definition's contract columns (input
    /// or output type, payload format). The drift never corrupts already-enqueued rows, which decode
    /// by their own stored format id; it affects enqueues made from then on and the CLR type old rows
    /// dispatch into. Default <c>Warn</c>.
    /// </summary>
    public PayloadContractDriftMode PayloadContractDriftMode { get; set; } = PayloadContractDriftMode.Warn;

    /// <summary>
    /// Explicit manifest generation (a UTC build timestamp) for this worker, the monotonic governor
    /// for definition promotion. When null, the runtime falls back to the entry assembly's file
    /// last-write-time, a convenience default that can vary across copy, publish, or container-image
    /// restore. Set this explicitly for single-file or AOT publishes, or whenever you need
    /// deterministic or CI-stamped ordering. Never derived from process start time.
    /// </summary>
    public DateTime? ManifestGenerationUtc { get; set; }

    /// <summary>
    /// Per-process; must differ across a rolling deploy (it identifies the build). Opaque deployment
    /// version (git SHA, build tag) written to <c>workers.deployment_version</c>. When <c>null</c>,
    /// the runtime derives it from the worker assembly's informational version.
    /// </summary>
    public string? DeploymentVersion { get; set; }

    /// <summary>
    /// Worker initialization measures the host-vs-database clock offset and throws when it exceeds the
    /// framework fail threshold (10s; 2s only warns), because a drifted host clock silently corrupts
    /// lease-expiry and schedule math. Set this <c>true</c> to downgrade that startup failure to a
    /// warning when the skew is understood and accepted. Default <c>false</c> (fail-loud).
    /// </summary>
    public bool AllowClockSkew { get; set; }

    /// <summary>
    /// Selects the claim/dispatch strategy for the worker's execution loop.
    /// <see cref="ExecutionProfile.Buffered"/> (default) is the conservative two-phase path.
    /// <see cref="ExecutionProfile.Direct"/> uses the combined claim-execute path with no Dispatched
    /// visibility window. <see cref="ExecutionProfile.Bulk"/> is Direct plus group-committed completions
    /// with relaxed completion durability; use it only for idempotent or safely repeatable work.
    /// </summary>
    public ExecutionProfile ExecutionProfile { get; set; } = ExecutionProfile.Buffered;

    /// <summary>
    /// <see cref="ExecutionProfile.Bulk"/> only. Maximum number of simple terminal completions buffered
    /// before the flusher group-commits them in one transaction. Amortizes the per-job completion commit;
    /// commit cost has sharp diminishing returns past ~100, and a larger batch widens the crash
    /// re-execution window. Fixed at 100.
    /// </summary>
    internal int BatchCompletionSize { get; set; } = 100;

    /// <summary>
    /// <see cref="ExecutionProfile.Bulk"/> only. How long a flusher accumulates its batch before a
    /// flush is forced (so a trickle still settles promptly). This bounds batch accumulation, not
    /// end-to-end buffered latency: a completion can additionally wait behind a slow store call. That
    /// wait is lease-safe while the worker lives (the heartbeat renews every row the worker leases,
    /// flushed or not); a crash loses the buffer under Bulk's at-least-once contract. Stays well below
    /// the lease window so buffering is a small fraction of it. Fixed at 250ms.
    /// </summary>
    internal TimeSpan BatchCompletionInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <see cref="ExecutionProfile.Bulk"/> only. Maximum accumulated <c>results</c> bytes buffered
    /// before a flush is forced, bounding the batch transaction regardless of individual result sizes
    /// (100 results at the 1 MiB cap would otherwise be ~100 MiB in one commit). A single result larger
    /// than this still flushes alone. Fixed at 4 MiB.
    /// </summary>
    internal int BatchCompletionMaxBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>
/// Worker behavior when an eligible registration would change a definition's contract columns
/// (input or output type, payload format) relative to the stored row.
/// </summary>
public enum PayloadContractDriftMode : byte
{
    /// <summary>Apply the change and log a warning. The default.</summary>
    Warn = 1,

    /// <summary>Fail worker startup before any catalog write.</summary>
    Fail = 2,
}

/// <summary>
/// Behavior for the startup check that every alerting definition resolves to a configured alert channel
/// in its namespace.
/// </summary>
public enum AlertChannelValidationMode : byte
{
    /// <summary>Skip the routing check entirely.</summary>
    Off = 0,

    /// <summary>Log a structured warning per definition that routes to an unregistered channel; continue.</summary>
    Warn = 1,

    /// <summary>Throw at worker initialization if any alerting definition routes to an unregistered channel.</summary>
    Fail = 2,
}

/// <summary>
/// Selects the claim/dispatch strategy for the worker's execution loop; see
/// JobsOptions.ExecutionProfile. On networked providers (Postgres, SQL Server), Buffered and Direct
/// keep per-job completion durable. Bulk relaxes completion durability by batching successful terminal
/// writes, so a crash can re-run handler work whose completion was not flushed. On SQLite, Direct
/// additionally sets PRAGMA synchronous = NORMAL (vs FULL on Buffered), trading a power-loss window
/// for far fewer commit fsyncs.
/// </summary>
public enum ExecutionProfile : byte
{
    /// <summary>Buffered two-phase: batched claim to Dispatched, Channel, per-job start_execution. Fully durable. Default.</summary>
    Buffered = 0,

    /// <summary>Combined claim-execute via a semaphore coordinator; no Dispatched window. Lower per-job latency; on SQLite relaxes commit fsync to PRAGMA synchronous = NORMAL.</summary>
    Direct = 1,

    /// <summary>
    /// Direct plus group-committed completions: simple terminal completions are buffered and flushed in one
    /// set-based transaction (every <c>BatchCompletionSize</c> jobs, <c>BatchCompletionInterval</c>, or
    /// <c>BatchCompletionMaxBytes</c>, whichever first). Relaxed durability: a crash loses the unflushed
    /// buffer, so those jobs stay Executing and <c>sys.recovery</c> re-runs them (at-least-once). For high-volume,
    /// cheap-to-re-run, idempotent work; not for exactly-once or per-job-atomic finalization. Completions
    /// needing a cross-row side effect (parent child-done latch) or a control branch
    /// (recurring, re-arm, suspend, handler fail/cancel/pause) fall back to the per-job complete_execution.
    /// On SQLite, behaves as Direct.
    /// </summary>
    Bulk = 2,
}
