namespace Acta.Relational.Schema;

internal static partial class ActaSchema
{
    internal static partial class Sql
    {
        // checkpoint_slot dispatch discriminator (CheckpointSlotAction); never persisted.
        public static readonly DbValueSpec<short> SlotAction = new(
            ParameterName: "p_action",
            Kind: DbKind.Int16,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> ClaimLimit = new(
            ParameterName: "p_claim_limit",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<string> NamespaceName = new(
            ParameterName: "p_namespace_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<string?> TenantKeyFilter = new(
            ParameterName: "p_tenant_key",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // Nullable scope targets for the settings operations: null means the scope narrows no further.
        public static readonly DbValueSpec<string?> ScopeNamespaceName = new(
            ParameterName: "p_namespace_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> ScopeJobName = new(
            ParameterName: "p_job_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string> JobName = new(
            ParameterName: "p_job_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<byte?> PriorityOverride = new(
            ParameterName: "p_priority_override",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<bool> ExecutionSucceeded = new(
            ParameterName: "p_execution_succeeded",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // start_step at-most-once switch: when true, a re-entered pending step is terminalized
        // Interrupted rather than re-invoked. Passed in each call; never persisted on the row.
        public static readonly DbValueSpec<bool> AtMostOnce = new(
            ParameterName: "p_at_most_once",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> LeaseTtlSeconds = new(
            ParameterName: "p_lease_ttl_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> DeadAfterSeconds = new(
            ParameterName: "p_dead_after_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> LeasedByWorkerId = new(
            ParameterName: "p_leased_by_worker_id",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // Claim routines can either transition straight to execution for the combined loop or land
        // in the dispatched state for buffered executor startup.
        public static readonly DbValueSpec<bool> StartExecuting = new(
            ParameterName: "p_start_executing",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> WorkerId = new(
            ParameterName: "p_worker_id",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // extend_worker_leases drain flag: true flips an Active worker to Draining as the heartbeat
        // refreshes its leases, so a graceful stop surfaces the draining phase without a dedicated routine.
        public static readonly DbValueSpec<bool> Draining = new(
            ParameterName: "p_draining",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // complete_execution re-arm mode: non-null status_code selects the re-arm branch
        // (150=Rescheduled, 151=Suspended); the host derives next_run_at_utc from the resume instant
        // or db_now + delay_seconds.
        public static readonly DbValueSpec<byte?> RescheduleStatusCode = new(
            ParameterName: "p_reschedule_status_code",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<int?> RescheduleDelaySeconds = new(
            ParameterName: "p_reschedule_delay_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> RescheduleResumeAtUtc = new(
            ParameterName: "p_reschedule_resume_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // complete_execution signal-suspend: non-null alongside reschedule_status_code = 151 selects the
        // signal branch. The awaited slot is locked and re-checked so a raise that arrived while the
        // handler was still Executing lands the Job Ready instead of stranding it Suspended.
        public static readonly DbValueSpec<string?> WaitSignalName = new(
            ParameterName: "p_wait_signal_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // complete_execution handler-control: non-null target Status (200 Failed / 220 Cancelled /
        // 30 Paused) for a deliberate ctx.FailAsync / CancelAsync / PauseAsync termination. Takes the
        // non-recurring path; overrides to_status and emits the matching lifecycle event.
        public static readonly DbValueSpec<byte?> HandlerStatusCode = new(
            ParameterName: "p_handler_status_code",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // arm_or_consume_sleep_timer: the wait length / absolute resume instant the routine resolves to
        // a stored due_at_utc under a job-level lock.
        public static readonly DbValueSpec<int?> SleepDelaySeconds = new(
            ParameterName: "p_delay_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> SleepResumeAtUtc = new(
            ParameterName: "p_resume_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // Step completion receives the outcome discriminator and the live-resolved retry policy used
        // to decide retry versus exhaustion. The application precomputes jittered backoff seconds;
        // storage compares the delayed retry time against the retry window.
        public static readonly DbValueSpec<bool> StepSucceeded = new(
            ParameterName: "p_succeeded",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> StepRetryDelaySeconds = new(
            ParameterName: "p_delay_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<short> StepMaxAttempts = new(
            ParameterName: "p_max_attempts",
            Kind: DbKind.Int16,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int?> StepRetryWindowSeconds = new(
            ParameterName: "p_retry_window_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // complete_execution / cancel_job retention stamping: non-null seconds added to db_now to set
        // runtimes.retention_until_utc at a terminal landing. NULL leaves the column untouched.
        public static readonly DbValueSpec<int?> RetentionSeconds = new(
            ParameterName: "p_retention_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // complete_execution final status / next-run / failure-count. On the non-recurring scalar path
        // p_final_status stays NULL (keeps the recurring branch off) and p_job_next_run_at_utc NULL; the
        // one-shot retry path passes p_failure_count = the bumped count so the routine's
        // COALESCE(p_failure_count, failure_count) persists it. NULL leaves failure_count untouched.
        public static readonly DbValueSpec<byte?> FinalStatus = new(
            ParameterName: "p_final_status",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> JobNextRunAtUtc = new(
            ParameterName: "p_job_next_run_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<short?> FailureCount = new(
            ParameterName: "p_failure_count",
            Kind: DbKind.Int16,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // sys.alerts projection cursor: the highest events.id consumed on the prior tick; the read
        // returns rows with id strictly greater, so the monotonic event id is the resumable watermark.
        public static readonly DbValueSpec<long> CursorEventId = new(
            ParameterName: "p_cursor_event_id",
            Kind: DbKind.Int64,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // sys.alerts batch cap: max rows the projection / delivery read returns per tick.
        public static readonly DbValueSpec<int> AlertBatchSize = new(
            ParameterName: "p_alert_batch_size",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // lineage-map direct-children cap: the read fetches ChildLimit + 1 so the caller can flag a truncated set.
        public static readonly DbValueSpec<int> ChildFetchLimit = new(
            ParameterName: "p_child_limit",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // purge_expired_data sweep knobs.
        public static readonly DbValueSpec<int> EventsRetentionDays = new(
            ParameterName: "p_events_retention_days",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> AlertRetentionDays = new(
            ParameterName: "p_alert_retention_days",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> WorkerRetentionSeconds = new(
            ParameterName: "p_worker_retention_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> PurgeBatchSize = new(
            ParameterName: "p_batch_size",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> PurgeMaxIterations = new(
            ParameterName: "p_max_iterations",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // List-read paging: rows requested per page plus one row to detect HasMore.
        public static readonly DbValueSpec<int> PageTake = new(
            ParameterName: "p_take",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        /// <summary>Last-seen age in seconds after which a live worker counts as stale.</summary>
        public static readonly DbValueSpec<int> StaleAfterSeconds = new(
            ParameterName: "p_stale_after_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        /// <summary>Window ahead of the database clock for the due-soon schedule count.</summary>
        public static readonly DbValueSpec<int> DueSoonSeconds = new(
            ParameterName: "p_due_soon_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<string?> NamespaceFilter = new(
            ParameterName: "p_namespace_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> JobNameFilter = new(
            ParameterName: "p_job_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read correlation filter: non-null restricts to jobs whose correlation_key matches exactly.
        // Sized to the correlation_key column (64); matched verbatim (the value is never canonicalized).
        public static readonly DbValueSpec<string?> CorrelationKeyFilter = new(
            ParameterName: "p_correlation_key",
            Kind: DbKind.AsciiString,
            Size: 64,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read tag filters encoded as JSON rows with canonical tag name and internal value_search.
        // NULL means no tag filter. The operation SQL parses this provider-locally.
        public static readonly DbValueSpec<string?> TagFiltersJson = new(
            ParameterName: "p_tag_filters",
            Kind: DbKind.UnicodeString,
            Size: -1,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<byte> TagTargetScopeCode = new(
            ParameterName: "p_scope_code",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<long?> TagTargetLookupId = new(
            ParameterName: "p_lookup_id",
            Kind: DbKind.Int64,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> TagTargetLookupName = new(
            ParameterName: "p_lookup_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<byte> TagMutation = new(
            ParameterName: "p_mutation",
            Kind: DbKind.Byte,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<string> TagItemsJson = new(
            ParameterName: "p_items_json",
            Kind: DbKind.UnicodeString,
            Size: -1,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // List-read parent filter: non-null restricts to the direct children of one job (ix_jobs_parent).
        public static readonly DbValueSpec<long?> ParentIdFilter = new(
            ParameterName: "p_parent_id",
            Kind: DbKind.Int64,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read keyset cursors: the last returned row's order-key values; all NULL on page one.
        public static readonly DbValueSpec<DateTime?> CursorCreatedAtUtc = new(
            ParameterName: "p_cursor_created_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> CursorNextRunAtUtc = new(
            ParameterName: "p_cursor_next_run_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> CursorLastSeenAtUtc = new(
            ParameterName: "p_cursor_last_seen_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // ListJobEvents created-instant range: inclusive lower bound / exclusive upper bound; NULL skips each.
        public static readonly DbValueSpec<DateTime?> EventCreatedFromUtc = new(
            ParameterName: "p_created_from_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<DateTime?> EventCreatedToUtc = new(
            ParameterName: "p_created_to_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<long?> CursorId = new(
            ParameterName: "p_cursor_id",
            Kind: DbKind.Int64,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<int?> CursorIntId = new(
            ParameterName: "p_cursor_int_id",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> CursorNamespaceName = new(
            ParameterName: "p_cursor_namespace_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> CursorTenantKey = new(
            ParameterName: "p_cursor_tenant_key",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> CursorJobName = new(
            ParameterName: "p_cursor_job_name",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read tri-state flags: true applies the restriction, NULL skips it.
        public static readonly DbValueSpec<bool?> LiveOnlyFlag = new(
            ParameterName: "p_live_only",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<bool?> UnresolvedOnlyFlag = new(
            ParameterName: "p_unresolved_only",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // ListJobs tri-state flags: true restricts to terminal rows / rows with a live schedule
        // attached; NULL skips each (false is folded to NULL before binding).
        public static readonly DbValueSpec<bool?> TerminalOnlyFlag = new(
            ParameterName: "p_terminal_only",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<bool?> RecurringOnlyFlag = new(
            ParameterName: "p_recurring_only",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read tri-state flag: null skips the filter, true/false restrict to acknowledged/unacknowledged
        // rows (unlike the "only" flags above, both non-null states are meaningful, not just true).
        public static readonly DbValueSpec<bool?> AcknowledgedFilter = new(
            ParameterName: "p_acknowledged",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read total toggle: when requested, storage computes the filter-wide row count into the
        // second result set. Skipping the total keeps a page read from paying that cost.
        public static readonly DbValueSpec<bool?> IncludeTotalFlag = new(
            ParameterName: "p_include_total",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // Overview slow-count toggle: non-null computes full-scope totals; NULL skips them.
        public static readonly DbValueSpec<bool?> IncludeSlowCountsFlag = new(
            ParameterName: "p_include_slow_counts",
            Kind: DbKind.Boolean,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // List-read name search: non-null restricts to rows whose name LIKE the bound pattern (the
        // caller wraps the term in '%' on both sides, so this is a contains match, matching how the
        // tenant list searches); NULL matches all. Names are kebab and the filter validators reject
        // '%' and '_', so a caller cannot smuggle LIKE wildcards through the term.
        public static readonly DbValueSpec<string?> NameSearchFilter = new(
            ParameterName: "p_name_search",
            Kind: DbKind.AsciiString,
            Size: 256,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // Tenant list free-text search: non-null is a pre-lowercased '%term%' LIKE pattern matched
        // (case-insensitively, via LOWER() on the columns) against tenant_key / display_name /
        // description; NULL matches all. Unicode so it can carry display_name / description text.
        public static readonly DbValueSpec<string?> TenantSearch = new(
            ParameterName: "p_search",
            Kind: DbKind.UnicodeString,
            Size: 512,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // set_schedule_overrides CAS guard: the caller's expected schedules.version; a mismatch rejects
        // with the row's current state instead of writing.
        public static readonly DbValueSpec<int> ExpectedScheduleVersion = new(
            ParameterName: "p_expected_version",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // Admin metadata CAS guard: the caller's expected row version; a mismatch rejects with the row's current version.
        public static readonly DbValueSpec<int> ExpectedRowVersion = new(
            ParameterName: "p_expected_version",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // set_schedule_overrides full-set override values: NULL clears the respective override column.
        // Sized to match schedules.expression / schedules.time_zone_id (the columns they fall back to).
        public static readonly DbValueSpec<string?> ScheduleExpressionOverride = new(
            ParameterName: "p_expression",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<string?> ScheduleTimeZoneIdOverride = new(
            ParameterName: "p_time_zone_id",
            Kind: DbKind.AsciiString,
            Size: 128,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // set_schedule_overrides: the target schedule's own cursor, recomputed in C# from the new
        // effective expression/time zone (distinct from p_job_next_run_at_utc, the owning slot's MIN).
        public static readonly DbValueSpec<DateTime?> ScheduleNextRunAtUtc = new(
            ParameterName: "p_schedule_next_run_at_utc",
            Kind: DbKind.UtcInstant,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );
    }
}
