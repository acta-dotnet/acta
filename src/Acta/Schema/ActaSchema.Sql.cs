namespace Acta;

internal static partial class ActaSchema
{
    internal static partial class Sql
    {
        /// <summary>
        /// access_checkpoint dispatch discriminator (AccessCheckpointAction); never persisted.
        /// </summary>
        public static readonly DbValueSpec<short> SlotAction = new("p_action", DbKind.Int16);

        public static readonly DbValueSpec<int> ClaimLimit = new("p_claim_limit", DbKind.Int32);

        public static readonly DbValueSpec<DateTime?> ClaimFromUtc = new("p_claim_from_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<string> NamespaceName = new("p_namespace_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> TenantNameFilter = new("p_tenant_name", DbKind.AsciiString, 128);

        /// <summary>
        /// start_step at-most-once switch: when true, a re-entered pending step is terminalized
        /// Interrupted rather than re-invoked. Passed in each call; never persisted on the row.
        /// </summary>
        public static readonly DbValueSpec<bool> AtMostOnce = new("p_at_most_once", DbKind.Boolean);

        public static readonly DbValueSpec<int> LeaseTtlSeconds = new("p_lease_ttl_seconds", DbKind.Int32);

        /// <summary>
        /// acquire_slot's counted-key prefix: the whole lock key minus its <c>.{slot}</c> tail, which
        /// the routine appends. Sized like <c>locks.lock_key</c> so a prefix can never bind wider.
        /// </summary>
        public static readonly DbValueSpec<string> LockKeyPrefix = new("p_lock_key_prefix", DbKind.AsciiString, 256);

        /// <summary>How many slots the counted key has; acquire_slot scans 0..n-1 in order.</summary>
        public static readonly DbValueSpec<int> SlotCount = new("p_slot_count", DbKind.Int32);

        /// <summary>
        /// Whole milliseconds between two rate admissions; reserve_rate moves the bucket by exactly
        /// this much per request. At least 1, which is what caps a declared rate at 1000 per second.
        /// </summary>
        public static readonly DbValueSpec<int> RateIntervalMilliseconds = new("p_rate_interval_ms", DbKind.Int32);

        /// <summary>How many admissions an idle bucket may hand out back to back: the declared count.</summary>
        public static readonly DbValueSpec<int> RateBurst = new("p_rate_burst", DbKind.Int32);

        /// <summary>
        /// How long past its turn a rate reservation stays sweepable-proof. The caller passes the
        /// worker lease TTL, which bounds how late a live job can come back for a turn it holds.
        /// </summary>
        public static readonly DbValueSpec<int> RateGraceSeconds = new("p_grace_seconds", DbKind.Int32);

        public static readonly DbValueSpec<int> DeadAfterSeconds = new("p_dead_after_seconds", DbKind.Int32);

        public static readonly DbValueSpec<int> LeasedByWorkerId = new("p_leased_by_worker_id", DbKind.Int32);

        public static readonly DbValueSpec<int> WorkerId = new("p_worker_id", DbKind.Int32);

        /// <summary>
        /// enqueue_job's parent fence: the execution number and worker of the parent attempt starting the child, both
        /// NULL for an enqueue no handler makes. Set, they hold the insert to that attempt (docs/sql-internals.md,
        /// "Execution fence").
        /// </summary>
        public static readonly DbValueSpec<int?> ParentExecutionNumber = new("p_parent_execution_number", DbKind.Int32);

        public static readonly DbValueSpec<int?> ParentWorkerId = new("p_parent_worker_id", DbKind.Int32);

        /// <summary>
        /// extend_worker_leases drain flag: true flips an Active worker to Draining as the heartbeat
        /// refreshes its leases, so a graceful stop surfaces the draining phase without a dedicated routine.
        /// </summary>
        public static readonly DbValueSpec<bool> Draining = new("p_draining", DbKind.Boolean);

        /// <summary>
        /// arm_or_consume_sleep_timer: the wait length / absolute resume instant the routine resolves to
        /// a stored due_at_utc under a job-level lock.
        /// </summary>
        public static readonly DbValueSpec<int?> SleepDelaySeconds = new("p_delay_seconds", DbKind.Int32);

        public static readonly DbValueSpec<DateTime?> SleepResumeAtUtc = new("p_resume_at_utc", DbKind.UtcInstant);

        /// <summary>
        /// wait_signal: the bounded wait's length in DB-clock seconds, resolved to the slot's absolute
        /// due_at_utc only when the Pending row is first inserted. Null arms an unbounded wait, and a
        /// re-entry never re-reads it, which is what makes replay unable to extend the expiration.
        /// </summary>
        public static readonly DbValueSpec<int?> WaitTimeoutSeconds = new("p_timeout_seconds", DbKind.Int32);

        /// <summary>
        /// Step completion receives the outcome discriminator and the live-resolved retry policy used
        /// to decide retry versus exhaustion. The application precomputes jittered backoff seconds;
        /// storage compares the delayed retry time against the retry window.
        /// </summary>
        public static readonly DbValueSpec<bool> StepSucceeded = new("p_succeeded", DbKind.Boolean);

        public static readonly DbValueSpec<int> StepRetryDelaySeconds = new("p_delay_seconds", DbKind.Int32);

        public static readonly DbValueSpec<int> StepMaxAttempts = new("p_max_attempts", DbKind.Int32);

        public static readonly DbValueSpec<int?> StepRetryWindowSeconds = new("p_retry_window_seconds", DbKind.Int32);

        /// <summary>The recurring slot's next run, as the schedule verbs recompute it.</summary>
        public static readonly DbValueSpec<DateTime?> JobNextRunAtUtc = new("p_job_next_run_at_utc", DbKind.UtcInstant);

        /// <summary>
        /// sys.alerts projection cursor, id half: the events.id consumed last on the prior tick. It only
        /// breaks the tie among rows sharing the cursor's instant; AlertCursorUtc is what orders the walk.
        /// </summary>
        public static readonly DbValueSpec<long> CursorEventId = new("p_cursor_event_id", DbKind.Int64);

        /// <summary>
        /// sys.alerts projection cursor, instant half: the created_at_utc of the last event consumed, or
        /// the horizon a short batch advanced to. Paired with CursorEventId; the read takes rows stamped
        /// after it, or stamped the same under a higher id, so an id that inverted against its stamp is
        /// never stepped over.
        /// </summary>
        public static readonly DbValueSpec<DateTime> AlertCursorUtc = new("p_cursor_utc", DbKind.UtcInstant);

        /// <summary>
        /// sys.alerts projection source: the id of the events row being projected into an alert. Null for a
        /// manual raise, which has no event behind it. The raise and auto-resolve compare it against the
        /// alert's stored last_projected_event_id so a replayed event changes nothing.
        /// </summary>
        public static readonly DbValueSpec<long?> SourceEventId = new("p_source_event_id", DbKind.Int64);

        /// <summary>
        /// sys.alerts safe horizon: the instant the projection read stops at, computed once per pass
        /// from the database clock less the command-timeout lag (<see cref="AlertsOperations.SafeHorizonLag"/>)
        /// and handed to every read of that pass, so an event whose id was allocated before a still-open
        /// transaction's is never stepped over, and no two reads of one pass can disagree on the boundary.
        /// </summary>
        public static readonly DbValueSpec<DateTime> AlertHorizonUtc = new("p_horizon_utc", DbKind.UtcInstant);

        /// <summary>
        /// sys.alerts batch cap: max rows the projection / delivery read returns per tick.
        /// </summary>
        public static readonly DbValueSpec<int> AlertBatchSize = new("p_alert_batch_size", DbKind.Int32);

        /// <summary>
        /// lineage-map direct-children cap: the read fetches ChildLimit + 1 so the caller can flag a truncated set.
        /// </summary>
        public static readonly DbValueSpec<int> ChildFetchLimit = new("p_child_limit", DbKind.Int32);

        /// <summary>
        /// purge_expired_data batch parameters.
        /// </summary>
        public static readonly DbValueSpec<int> PurgeSection = new("p_section", DbKind.Int32);

        public static readonly DbValueSpec<DateTime> PurgeCutoffUtc = new("p_cutoff_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<int> PurgeBatchSize = new("p_batch_size", DbKind.Int32);

        /// <summary>The jobs walk's cursor: the section reads the jobs_state rows below this job id, or from the newest when zero.</summary>
        public static readonly DbValueSpec<long> PurgeBeforeJobId = new("p_before_job_id", DbKind.Int64);

        /// <summary>
        /// List-read paging: rows requested per page plus one row to detect HasMore.
        /// </summary>
        public static readonly DbValueSpec<int> PageTake = new("p_take", DbKind.Int32);

        /// <summary>Last-seen age in seconds after which a live worker counts as stale.</summary>
        public static readonly DbValueSpec<int> StaleAfterSeconds = new("p_stale_after_seconds", DbKind.Int32);

        /// <summary>Window ahead of the database clock for the due-soon schedule count.</summary>
        public static readonly DbValueSpec<int> DueSoonSeconds = new("p_due_soon_seconds", DbKind.Int32);

        public static readonly DbValueSpec<string?> NamespaceFilter = new("p_namespace_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> JobNameFilter = new("p_job_name", DbKind.AsciiString, 128);

        /// <summary>
        /// List-read correlation filter: non-null restricts to jobs whose correlation_key matches exactly.
        /// Sized to the correlation_key column (64); the handler canonicalizes it as a key, so it matches bytewise.
        /// </summary>
        public static readonly DbValueSpec<string?> CorrelationKeyFilter = new("p_correlation_key", DbKind.AsciiString, 64);

        /// <summary>
        /// List-read lane filter: non-null restricts to jobs in lanes of this name. Sized to lanes.lane_name
        /// (128) and bound canonical, as the name is stored.
        /// </summary>
        public static readonly DbValueSpec<string?> LaneFilter = new("p_lane_filter", DbKind.AsciiString, 128);

        /// <summary>
        /// List-read tag filters encoded as JSON rows with canonical tag_name and internal tag_value_search.
        /// NULL means no tag filter. The operation SQL parses this provider-locally.
        /// </summary>
        public static readonly DbValueSpec<string?> TagFiltersJson = new("p_tag_filters", DbKind.UnicodeString, -1);

        public static readonly DbValueSpec<byte> TagTargetEntityCode = new("p_entity_code", DbKind.Byte);

        public static readonly DbValueSpec<long?> TagTargetLookupId = new("p_lookup_id", DbKind.Int64);

        public static readonly DbValueSpec<string?> TagTargetLookupName = new("p_lookup_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string> TagItemsJson = new("p_items_json", DbKind.UnicodeString, -1);

        /// <summary>
        /// List-read parent filter: non-null restricts to the direct children of one job (ix_jobs_parent).
        /// </summary>
        public static readonly DbValueSpec<long?> ParentJobIdFilter = new("p_parent_job_id", DbKind.Int64);

        /// <summary>
        /// List-read keyset cursors: the last returned row's order-key values; all NULL on page one.
        /// </summary>
        public static readonly DbValueSpec<DateTime?> CursorCreatedAtUtc = new("p_cursor_created_at_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<DateTime?> CursorNextOccurrenceAtUtc = new("p_cursor_next_occurrence_at_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<DateTime?> CursorLastSeenAtUtc = new("p_cursor_last_seen_at_utc", DbKind.UtcInstant);

        /// <summary>
        /// ListEvents created-instant range: inclusive lower bound / exclusive upper bound; NULL skips each.
        /// </summary>
        public static readonly DbValueSpec<DateTime?> EventCreatedFromUtc = new("p_created_from_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<DateTime?> EventCreatedToUtc = new("p_created_to_utc", DbKind.UtcInstant);

        public static readonly DbValueSpec<long?> CursorId = new("p_cursor_id", DbKind.Int64);

        public static readonly DbValueSpec<int?> CursorIntId = new("p_cursor_int_id", DbKind.Int32);

        public static readonly DbValueSpec<string?> CursorNamespaceName = new("p_cursor_namespace_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> CursorTenantName = new("p_cursor_tenant_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> CursorJobName = new("p_cursor_job_name", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> CursorScheduleName = new("p_cursor_schedule_name", DbKind.AsciiString, 128);

        /// <summary>
        /// List-read tri-state flags: true applies the restriction, NULL skips it.
        /// </summary>
        public static readonly DbValueSpec<bool?> LiveOnlyFlag = new("p_live_only", DbKind.Boolean);

        public static readonly DbValueSpec<bool?> UnresolvedOnlyFlag = new("p_unresolved_only", DbKind.Boolean);

        /// <summary>
        /// ListJobs tri-state flags: true restricts to terminal rows / rows with a live schedule
        /// attached; NULL skips each (false is folded to NULL before binding).
        /// </summary>
        public static readonly DbValueSpec<bool?> TerminalOnlyFlag = new("p_terminal_only", DbKind.Boolean);

        public static readonly DbValueSpec<bool?> RecurringOnlyFlag = new("p_recurring_only", DbKind.Boolean);

        /// <summary>
        /// List-read tri-state flag: null skips the filter, true/false restrict to acknowledged/unacknowledged
        /// rows (unlike the "only" flags above, both non-null states are meaningful, not just true).
        /// </summary>
        public static readonly DbValueSpec<bool?> AcknowledgedFilter = new("p_acknowledged", DbKind.Boolean);

        /// <summary>
        /// List-read total toggle: when requested, storage computes the filter-wide row count into the
        /// second result set. Skipping the total keeps a page read from paying that cost.
        /// </summary>
        public static readonly DbValueSpec<bool?> IncludeTotalFlag = new("p_include_total", DbKind.Boolean);

        /// <summary>
        /// List-read name search: non-null restricts to rows whose name LIKE the bound pattern (the
        /// caller wraps the term in '%' on both sides, so this is a contains match, matching how the
        /// tenant list searches); NULL matches all. Names are kebab and the filter validators reject
        /// '%' and '_', so a caller cannot smuggle LIKE wildcards through the term.
        /// </summary>
        public static readonly DbValueSpec<string?> NameSearchFilter = new("p_name_search", DbKind.AsciiString, 256);

        /// <summary>
        /// Tenant list free-text search: non-null is a pre-lowercased '%term%' LIKE pattern matched
        /// (case-insensitively, via LOWER() on the columns) against tenant_name / display_name /
        /// description; NULL matches all. Unicode so it can carry display_name / description text.
        /// </summary>
        public static readonly DbValueSpec<string?> TenantSearch = new("p_search", DbKind.UnicodeString, 512);

        /// <summary>
        /// set_schedule_overrides CAS guard: the caller's expected schedules.version; a mismatch rejects
        /// with the row's current state instead of writing.
        /// </summary>
        public static readonly DbValueSpec<int> ExpectedScheduleVersion = new("p_expected_version", DbKind.Int32);

        /// <summary>
        /// Optional CAS guard for the job control verbs:
        /// null skips the version check (last write wins); non-null requires an existing row at exactly
        /// that version, else the write is rejected with VersionConflict (or NotFound when no row exists
        /// at the scope).
        /// </summary>
        public static readonly DbValueSpec<int?> ExpectedRowVersionOptional = new("p_expected_version", DbKind.Int32);

        /// <summary>
        /// Admin metadata CAS guard: the caller's expected row version; a mismatch rejects with the row's current version.
        /// </summary>
        public static readonly DbValueSpec<int> ExpectedRowVersion = new("p_expected_version", DbKind.Int32);

        /// <summary>
        /// set_schedule_overrides full-set override values: NULL clears the respective override column.
        /// Sized to match schedules.expression / schedules.time_zone_id (the columns they fall back to).
        /// </summary>
        public static readonly DbValueSpec<string?> ScheduleExpressionOverride = new("p_expression", DbKind.AsciiString, 128);

        public static readonly DbValueSpec<string?> ScheduleTimeZoneIdOverride = new("p_time_zone_id", DbKind.AsciiString, 128);

        /// <summary>
        /// park_outbox_signal supersede guard: a pending command whose park instant is at or before
        /// this cutoff (now minus WorkerDeadAfter) has had no live consumer for the whole dead window,
        /// so a new command may overwrite it; anything younger rejects instead.
        /// </summary>
        public static readonly DbValueSpec<DateTime> StaleBefore = new("p_stale_before_utc", DbKind.UtcInstant);

        /// <summary>PlanOutboxSignal's replacement value: the command it read, carrying the rows it targets.</summary>
        public static readonly DbValueSpec<byte[]> PlannedValue = new("p_planned_value", DbKind.BinaryPayload);

        /// <summary>
        /// retire_definition's details for each job.cancelled event it writes: the operator and the definition-retired
        /// reason, beside p_details, which the definition.retired event carries.
        /// </summary>
        public static readonly DbValueSpec<byte[]?> RetiredJobDetails = new("p_job_details", DbKind.BinaryPayload);

        /// <summary>ListEvents job filter: the job-subject events of one job id; NULL skips it.</summary>
        public static readonly DbValueSpec<long?> JobIdFilter = new("p_job_id", DbKind.Int64);

        /// <summary>
        /// The namespace's alert floor (GetAlertFloorResponse), bound as a (created_at_utc, event_id) pair.
        /// </summary>
        public static readonly DbValueSpec<DateTime> AlertFloorUtc = new("p_alert_floor_utc", DbKind.UtcInstant);

        /// <summary>The event id half of the alert floor; see <see cref="AlertFloorUtc"/>.</summary>
        public static readonly DbValueSpec<long> AlertFloorEventId = new("p_alert_floor_event_id", DbKind.Int64);

        /// <summary>
        /// A JSON array of <c>{"event_id", "job_id"}</c> objects naming every unresolved skip for the replay read.
        /// </summary>
        public static readonly DbValueSpec<string?> AlertPoisonEvents = new("p_alert_poison_events", DbKind.UnicodeString, -1);

        /// <summary>get_alert_floor's namespace, or NULL to take the namespace of <see cref="JobIdFilter"/>'s job.</summary>
        public static readonly DbValueSpec<int?> NamespaceIdFilter = new("p_namespace_id", DbKind.Int32);
    }
}
