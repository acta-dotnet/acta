namespace Acta.Runtime;

internal static partial class ActaSchema
{
    internal static partial class Sql
    {
        // A SQL-only parameter is a plain scalar: no precision or scale, and a width only on a string.
        private static DbValueSpec<T> Scalar<T>(string name, DbKind kind, bool nullable) => new(name, kind, null, null, null, nullable);

        private static DbValueSpec<T> Sized<T>(string name, DbKind kind, int size, bool nullable) =>
            new(name, kind, size, null, null, nullable);

        /// <summary>
        /// checkpoint_slot dispatch discriminator (CheckpointSlotAction); never persisted.
        /// </summary>
        public static readonly DbValueSpec<short> SlotAction = Scalar<short>("p_action", DbKind.Int16, nullable: false);

        public static readonly DbValueSpec<int> ClaimLimit = Scalar<int>("p_claim_limit", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<string> NamespaceName = Sized<string>(
            "p_namespace_name",
            DbKind.AsciiString,
            128,
            nullable: false
        );

        public static readonly DbValueSpec<string?> TenantKeyFilter = Sized<string?>(
            "p_tenant_key",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<bool> ExecutionSucceeded = Scalar<bool>(
            "p_execution_succeeded",
            DbKind.Boolean,
            nullable: false
        );

        /// <summary>
        /// start_step at-most-once switch: when true, a re-entered pending step is terminalized
        /// Interrupted rather than re-invoked. Passed in each call; never persisted on the row.
        /// </summary>
        public static readonly DbValueSpec<bool> AtMostOnce = Scalar<bool>("p_at_most_once", DbKind.Boolean, nullable: false);

        public static readonly DbValueSpec<int> LeaseTtlSeconds = Scalar<int>("p_lease_ttl_seconds", DbKind.Int32, nullable: false);

        /// <summary>
        /// acquire_slot's counted-key prefix: the whole lock key minus its <c>.{slot}</c> tail, which
        /// the routine appends. Sized like <c>locks.lock_key</c> so a prefix can never bind wider.
        /// </summary>
        public static readonly DbValueSpec<string> LockKeyPrefix = Sized<string>(
            "p_lock_key_prefix",
            DbKind.AsciiString,
            256,
            nullable: false
        );

        /// <summary>How many slots the counted key has; acquire_slot scans 0..n-1 in order.</summary>
        public static readonly DbValueSpec<int> SlotCount = Scalar<int>("p_slot_count", DbKind.Int32, nullable: false);

        /// <summary>
        /// Whole milliseconds between two rate admissions; reserve_rate moves the bucket by exactly
        /// this much per request. At least 1, which is what caps a declared rate at 1000 per second.
        /// </summary>
        public static readonly DbValueSpec<int> RateIntervalMilliseconds = Scalar<int>("p_rate_interval_ms", DbKind.Int32, nullable: false);

        /// <summary>How many admissions an idle bucket may hand out back to back: the declared count.</summary>
        public static readonly DbValueSpec<int> RateBurst = Scalar<int>("p_rate_burst", DbKind.Int32, nullable: false);

        /// <summary>
        /// How long past its turn a rate reservation stays sweepable-proof. The caller passes the
        /// worker lease TTL, which bounds how late a live job can come back for a turn it holds.
        /// </summary>
        public static readonly DbValueSpec<int> RateGraceSeconds = Scalar<int>("p_grace_seconds", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<int> DeadAfterSeconds = Scalar<int>("p_dead_after_seconds", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<int> LeasedByWorkerId = Scalar<int>("p_leased_by_worker_id", DbKind.Int32, nullable: false);

        /// <summary>
        /// Claim routines can either transition straight to execution for the combined loop or land
        /// in the dispatched state for buffered executor startup.
        /// </summary>
        public static readonly DbValueSpec<bool> StartExecuting = Scalar<bool>("p_start_executing", DbKind.Boolean, nullable: false);

        public static readonly DbValueSpec<int> WorkerId = Scalar<int>("p_worker_id", DbKind.Int32, nullable: false);

        /// <summary>
        /// extend_worker_leases drain flag: true flips an Active worker to Draining as the heartbeat
        /// refreshes its leases, so a graceful stop surfaces the draining phase without a dedicated routine.
        /// </summary>
        public static readonly DbValueSpec<bool> Draining = Scalar<bool>("p_draining", DbKind.Boolean, nullable: false);

        /// <summary>
        /// complete_execution re-arm mode: non-null status_code selects the re-arm branch
        /// (150=Rescheduled, 151=Suspended); the host derives next_run_at_utc from the resume instant
        /// or db_now + delay_seconds.
        /// </summary>
        public static readonly DbValueSpec<byte?> RescheduleStatusCode = Scalar<byte?>(
            "p_reschedule_status_code",
            DbKind.Byte,
            nullable: true
        );

        public static readonly DbValueSpec<int?> RescheduleDelaySeconds = Scalar<int?>(
            "p_reschedule_delay_seconds",
            DbKind.Int32,
            nullable: true
        );

        public static readonly DbValueSpec<DateTime?> RescheduleResumeAtUtc = Scalar<DateTime?>(
            "p_reschedule_resume_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        /// <summary>
        /// complete_execution signal-suspend: non-null alongside reschedule_status_code = 151 selects the
        /// signal branch. The awaited slot is locked and re-checked so a raise that arrived while the
        /// handler was still Executing lands the Job Ready instead of stranding it Suspended.
        /// </summary>
        public static readonly DbValueSpec<string?> WaitSignalName = Sized<string?>(
            "p_wait_signal_name",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        /// <summary>
        /// complete_execution handler-control: non-null target Status (200 Failed / 220 Cancelled /
        /// 30 Paused) for a deliberate ctx.FailAsync / CancelAsync / PauseAsync termination. Takes the
        /// non-recurring path; overrides to_status and emits the matching lifecycle event.
        /// </summary>
        public static readonly DbValueSpec<byte?> HandlerStatusCode = Scalar<byte?>("p_handler_status_code", DbKind.Byte, nullable: true);

        /// <summary>
        /// arm_or_consume_sleep_timer: the wait length / absolute resume instant the routine resolves to
        /// a stored due_at_utc under a job-level lock.
        /// </summary>
        public static readonly DbValueSpec<int?> SleepDelaySeconds = Scalar<int?>("p_delay_seconds", DbKind.Int32, nullable: true);

        public static readonly DbValueSpec<DateTime?> SleepResumeAtUtc = Scalar<DateTime?>(
            "p_resume_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        /// <summary>
        /// wait_signal: the bounded wait's length in DB-clock seconds, resolved to the slot's absolute
        /// due_at_utc only when the Pending row is first inserted. Null arms an unbounded wait, and a
        /// re-entry never re-reads it, which is what makes replay unable to extend the expiration.
        /// </summary>
        public static readonly DbValueSpec<int?> WaitTimeoutSeconds = Scalar<int?>("p_timeout_seconds", DbKind.Int32, nullable: true);

        /// <summary>
        /// Step completion receives the outcome discriminator and the live-resolved retry policy used
        /// to decide retry versus exhaustion. The application precomputes jittered backoff seconds;
        /// storage compares the delayed retry time against the retry window.
        /// </summary>
        public static readonly DbValueSpec<bool> StepSucceeded = Scalar<bool>("p_succeeded", DbKind.Boolean, nullable: false);

        public static readonly DbValueSpec<int> StepRetryDelaySeconds = Scalar<int>("p_delay_seconds", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<int> StepMaxAttempts = Scalar<int>("p_max_attempts", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<int?> StepRetryWindowSeconds = Scalar<int?>(
            "p_retry_window_seconds",
            DbKind.Int32,
            nullable: true
        );

        /// <summary>
        /// complete_execution / cancel_job retention stamping: non-null seconds added to db_now to set
        /// runtimes.retention_until_utc at a terminal landing. NULL leaves the column untouched.
        /// </summary>
        public static readonly DbValueSpec<int?> RetentionSeconds = Scalar<int?>("p_retention_seconds", DbKind.Int32, nullable: true);

        /// <summary>
        /// complete_execution final status / next-run / failure-count. On the non-recurring scalar path
        /// p_final_status stays NULL (keeps the recurring branch off) and p_job_next_run_at_utc NULL; the
        /// one-shot retry path passes p_failure_count = the bumped count so the routine's
        /// COALESCE(p_failure_count, failure_count) persists it. NULL leaves failure_count untouched.
        /// </summary>
        public static readonly DbValueSpec<byte?> FinalStatus = Scalar<byte?>("p_final_status", DbKind.Byte, nullable: true);

        public static readonly DbValueSpec<DateTime?> JobNextRunAtUtc = Scalar<DateTime?>(
            "p_job_next_run_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        public static readonly DbValueSpec<int?> FailureCount = Scalar<int?>("p_failure_count", DbKind.Int32, nullable: true);

        /// <summary>
        /// sys.alerts projection cursor, id half: the events.id consumed last on the prior tick. It only
        /// breaks the tie among rows sharing the cursor's instant; AlertCursorUtc is what orders the walk.
        /// </summary>
        public static readonly DbValueSpec<long> CursorEventId = Scalar<long>("p_cursor_event_id", DbKind.Int64, nullable: false);

        /// <summary>
        /// sys.alerts projection cursor, instant half: the created_at_utc of the last event consumed, or
        /// the horizon a short batch advanced to. Paired with CursorEventId; the read takes rows stamped
        /// after it, or stamped the same under a higher id, so an id that inverted against its stamp is
        /// never stepped over.
        /// </summary>
        public static readonly DbValueSpec<DateTime> AlertCursorUtc = Scalar<DateTime>("p_cursor_utc", DbKind.UtcInstant, nullable: false);

        /// <summary>
        /// sys.alerts projection source: the id of the events row being projected into an alert. Null for a
        /// manual raise, which has no event behind it. The raise and auto-resolve compare it against the
        /// alert's stored last_projected_event_id so a replayed event changes nothing.
        /// </summary>
        public static readonly DbValueSpec<long?> SourceEventId = Scalar<long?>("p_source_event_id", DbKind.Int64, nullable: true);

        /// <summary>
        /// sys.alerts safe horizon: the instant the projection read stops at, computed once per pass
        /// from the database clock less the command-timeout lag (GetAlertableEventsHandler.SafeHorizonLagSeconds)
        /// and handed to every read of that pass, so an event whose id was allocated before a still-open
        /// transaction's is never stepped over, and no two reads of one pass can disagree on the boundary.
        /// </summary>
        public static readonly DbValueSpec<DateTime> AlertHorizonUtc = Scalar<DateTime>(
            "p_horizon_utc",
            DbKind.UtcInstant,
            nullable: false
        );

        /// <summary>
        /// sys.alerts batch cap: max rows the projection / delivery read returns per tick.
        /// </summary>
        public static readonly DbValueSpec<int> AlertBatchSize = Scalar<int>("p_alert_batch_size", DbKind.Int32, nullable: false);

        /// <summary>
        /// lineage-map direct-children cap: the read fetches ChildLimit + 1 so the caller can flag a truncated set.
        /// </summary>
        public static readonly DbValueSpec<int> ChildFetchLimit = Scalar<int>("p_child_limit", DbKind.Int32, nullable: false);

        /// <summary>
        /// purge_expired_data batch parameters.
        /// </summary>
        public static readonly DbValueSpec<int> PurgeSection = Scalar<int>("p_section", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<DateTime> PurgeCutoffUtc = Scalar<DateTime>("p_cutoff_utc", DbKind.UtcInstant, nullable: false);

        public static readonly DbValueSpec<int> PurgeBatchSize = Scalar<int>("p_batch_size", DbKind.Int32, nullable: false);

        /// <summary>
        /// List-read paging: rows requested per page plus one row to detect HasMore.
        /// </summary>
        public static readonly DbValueSpec<int> PageTake = Scalar<int>("p_take", DbKind.Int32, nullable: false);

        /// <summary>Last-seen age in seconds after which a live worker counts as stale.</summary>
        public static readonly DbValueSpec<int> StaleAfterSeconds = Scalar<int>("p_stale_after_seconds", DbKind.Int32, nullable: false);

        /// <summary>Window ahead of the database clock for the due-soon schedule count.</summary>
        public static readonly DbValueSpec<int> DueSoonSeconds = Scalar<int>("p_due_soon_seconds", DbKind.Int32, nullable: false);

        public static readonly DbValueSpec<string?> NamespaceFilter = Sized<string?>(
            "p_namespace_name",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<string?> JobNameFilter = Sized<string?>("p_job_name", DbKind.AsciiString, 128, nullable: true);

        /// <summary>
        /// List-read correlation filter: non-null restricts to jobs whose correlation_key matches exactly.
        /// Sized to the correlation_key column (64); matched verbatim (the value is never canonicalized).
        /// </summary>
        public static readonly DbValueSpec<string?> CorrelationKeyFilter = Sized<string?>(
            "p_correlation_key",
            DbKind.AsciiString,
            64,
            nullable: true
        );

        /// <summary>
        /// List-read lane filter: non-null restricts to jobs in lanes of this name. Sized to lanes.name
        /// (128) and bound canonical, as the name is stored.
        /// </summary>
        public static readonly DbValueSpec<string?> LaneFilter = Sized<string?>("p_lane_filter", DbKind.AsciiString, 128, nullable: true);

        /// <summary>
        /// List-read tag filters encoded as JSON rows with canonical tag name and internal value_search.
        /// NULL means no tag filter. The operation SQL parses this provider-locally.
        /// </summary>
        public static readonly DbValueSpec<string?> TagFiltersJson = Sized<string?>(
            "p_tag_filters",
            DbKind.UnicodeString,
            -1,
            nullable: true
        );

        public static readonly DbValueSpec<byte> TagTargetScopeCode = Scalar<byte>("p_scope_code", DbKind.Byte, nullable: false);

        public static readonly DbValueSpec<long?> TagTargetLookupId = Scalar<long?>("p_lookup_id", DbKind.Int64, nullable: true);

        public static readonly DbValueSpec<string?> TagTargetLookupName = Sized<string?>(
            "p_lookup_name",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<string> TagItemsJson = Sized<string>("p_items_json", DbKind.UnicodeString, -1, nullable: false);

        /// <summary>
        /// List-read parent filter: non-null restricts to the direct children of one job (ix_jobs_parent).
        /// </summary>
        public static readonly DbValueSpec<long?> ParentIdFilter = Scalar<long?>("p_parent_id", DbKind.Int64, nullable: true);

        /// <summary>
        /// List-read keyset cursors: the last returned row's order-key values; all NULL on page one.
        /// </summary>
        public static readonly DbValueSpec<DateTime?> CursorCreatedAtUtc = Scalar<DateTime?>(
            "p_cursor_created_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        public static readonly DbValueSpec<DateTime?> CursorNextRunAtUtc = Scalar<DateTime?>(
            "p_cursor_next_run_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        public static readonly DbValueSpec<DateTime?> CursorLastSeenAtUtc = Scalar<DateTime?>(
            "p_cursor_last_seen_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        /// <summary>
        /// ListEvents created-instant range: inclusive lower bound / exclusive upper bound; NULL skips each.
        /// </summary>
        public static readonly DbValueSpec<DateTime?> EventCreatedFromUtc = Scalar<DateTime?>(
            "p_created_from_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        public static readonly DbValueSpec<DateTime?> EventCreatedToUtc = Scalar<DateTime?>(
            "p_created_to_utc",
            DbKind.UtcInstant,
            nullable: true
        );

        public static readonly DbValueSpec<long?> CursorId = Scalar<long?>("p_cursor_id", DbKind.Int64, nullable: true);

        public static readonly DbValueSpec<int?> CursorIntId = Scalar<int?>("p_cursor_int_id", DbKind.Int32, nullable: true);

        public static readonly DbValueSpec<string?> CursorNamespaceName = Sized<string?>(
            "p_cursor_namespace_name",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<string?> CursorTenantKey = Sized<string?>(
            "p_cursor_tenant_key",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<string?> CursorJobName = Sized<string?>(
            "p_cursor_job_name",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        /// <summary>
        /// List-read tri-state flags: true applies the restriction, NULL skips it.
        /// </summary>
        public static readonly DbValueSpec<bool?> LiveOnlyFlag = Scalar<bool?>("p_live_only", DbKind.Boolean, nullable: true);

        public static readonly DbValueSpec<bool?> UnresolvedOnlyFlag = Scalar<bool?>("p_unresolved_only", DbKind.Boolean, nullable: true);

        /// <summary>
        /// ListJobs tri-state flags: true restricts to terminal rows / rows with a live schedule
        /// attached; NULL skips each (false is folded to NULL before binding).
        /// </summary>
        public static readonly DbValueSpec<bool?> TerminalOnlyFlag = Scalar<bool?>("p_terminal_only", DbKind.Boolean, nullable: true);

        public static readonly DbValueSpec<bool?> RecurringOnlyFlag = Scalar<bool?>("p_recurring_only", DbKind.Boolean, nullable: true);

        /// <summary>
        /// List-read tri-state flag: null skips the filter, true/false restrict to acknowledged/unacknowledged
        /// rows (unlike the "only" flags above, both non-null states are meaningful, not just true).
        /// </summary>
        public static readonly DbValueSpec<bool?> AcknowledgedFilter = Scalar<bool?>("p_acknowledged", DbKind.Boolean, nullable: true);

        /// <summary>
        /// List-read total toggle: when requested, storage computes the filter-wide row count into the
        /// second result set. Skipping the total keeps a page read from paying that cost.
        /// </summary>
        public static readonly DbValueSpec<bool?> IncludeTotalFlag = Scalar<bool?>("p_include_total", DbKind.Boolean, nullable: true);

        /// <summary>
        /// Overview slow-count toggle: non-null computes full-scope totals; NULL skips them.
        /// </summary>
        public static readonly DbValueSpec<bool?> IncludeSlowCountsFlag = Scalar<bool?>(
            "p_include_slow_counts",
            DbKind.Boolean,
            nullable: true
        );

        /// <summary>
        /// List-read name search: non-null restricts to rows whose name LIKE the bound pattern (the
        /// caller wraps the term in '%' on both sides, so this is a contains match, matching how the
        /// tenant list searches); NULL matches all. Names are kebab and the filter validators reject
        /// '%' and '_', so a caller cannot smuggle LIKE wildcards through the term.
        /// </summary>
        public static readonly DbValueSpec<string?> NameSearchFilter = Sized<string?>(
            "p_name_search",
            DbKind.AsciiString,
            256,
            nullable: true
        );

        /// <summary>
        /// Tenant list free-text search: non-null is a pre-lowercased '%term%' LIKE pattern matched
        /// (case-insensitively, via LOWER() on the columns) against tenant_key / display_name /
        /// description; NULL matches all. Unicode so it can carry display_name / description text.
        /// </summary>
        public static readonly DbValueSpec<string?> TenantSearch = Sized<string?>("p_search", DbKind.UnicodeString, 512, nullable: true);

        /// <summary>
        /// set_schedule_overrides CAS guard: the caller's expected schedules.version; a mismatch rejects
        /// with the row's current state instead of writing.
        /// </summary>
        public static readonly DbValueSpec<int> ExpectedScheduleVersion = Scalar<int>("p_expected_version", DbKind.Int32, nullable: false);

        /// <summary>
        /// Optional CAS guard for the job control verbs:
        /// null skips the version check (last write wins); non-null requires an existing row at exactly
        /// that version, else the write is rejected with VersionConflict (or NotFound when no row exists
        /// at the scope).
        /// </summary>
        public static readonly DbValueSpec<int?> ExpectedRowVersionOptional = Scalar<int?>(
            "p_expected_version",
            DbKind.Int32,
            nullable: true
        );

        /// <summary>
        /// Admin metadata CAS guard: the caller's expected row version; a mismatch rejects with the row's current version.
        /// </summary>
        public static readonly DbValueSpec<int> ExpectedRowVersion = Scalar<int>("p_expected_version", DbKind.Int32, nullable: false);

        /// <summary>
        /// set_schedule_overrides full-set override values: NULL clears the respective override column.
        /// Sized to match schedules.expression / schedules.time_zone_id (the columns they fall back to).
        /// </summary>
        public static readonly DbValueSpec<string?> ScheduleExpressionOverride = Sized<string?>(
            "p_expression",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        public static readonly DbValueSpec<string?> ScheduleTimeZoneIdOverride = Sized<string?>(
            "p_time_zone_id",
            DbKind.AsciiString,
            128,
            nullable: true
        );

        /// <summary>
        /// park_outbox_signal supersede guard: a pending command whose park instant is at or before
        /// this cutoff (now minus WorkerDeadAfter) has had no live consumer for the whole dead window,
        /// so a new command may overwrite it; anything younger rejects instead.
        /// </summary>
        public static readonly DbValueSpec<DateTime> StaleBefore = Scalar<DateTime>(
            "p_stale_before_utc",
            DbKind.UtcInstant,
            nullable: false
        );

        /// <summary>
        /// set_schedule_overrides audit text: the rendered what-changed summary written to
        /// events.reason_message. A separate parameter from p_reason_message, which carries the
        /// operator's justification into schedules.reason_message on the same command.
        /// </summary>
        public static readonly DbValueSpec<string?> ChangeSummary = Sized<string?>(
            "p_change_summary",
            DbKind.UnicodeString,
            512,
            nullable: true
        );

        /// <summary>
        /// set_schedule_overrides: the target schedule's own cursor, recomputed in C# from the new
        /// effective expression/time zone (distinct from p_job_next_run_at_utc, the owning slot's MIN).
        /// </summary>
        public static readonly DbValueSpec<DateTime?> ScheduleNextRunAtUtc = Scalar<DateTime?>(
            "p_schedule_next_run_at_utc",
            DbKind.UtcInstant,
            nullable: true
        );
    }
}
