DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
/* Dueness compares at the columns' DATETIME2(3) precision: enqueue rounds stamps up to 0.5 ms into
   the future, so a full-precision @now can transiently see a just-enqueued Ready row as not yet due
   (ready_count would include it while oldest_ready_age_seconds missed it). */
DECLARE @due_now DATETIME2(3) = @now;
DECLARE @ns_id SMALLINT = NULL;

IF @p_namespace_name IS NOT NULL
    BEGIN
        SELECT @ns_id = id
        FROM {{schema}}.namespaces
        WHERE name = @p_namespace_name;
    END;

/* The two backlog counters below admit what claim_batch claims. Ready keeps its gauge meaning and
   counts whether or not it is due; a Suspended row counts only once its wait has actually expired, or
   a queue of long approval waits would read as backlog that no worker is behind on. */
/* Not shared text - claim_batch is an installed routine and this is an inline command - so the two
   are must-stay-in-sync twins; change one, change the other. */
SELECT
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.runtimes r
        WHERE
            (
                r.status_code = 10 /* JobStatusCode.Ready */
                OR (
                    r.status_code = 20 /* JobStatusCode.Suspended */
                    AND r.next_run_at_utc IS NOT NULL
                    AND r.next_run_at_utc <= @due_now
                )
            )
            AND (@p_namespace_name IS NULL OR r.namespace_id = @ns_id)
    ) AS ready_count,
    (
        SELECT CAST(DATEDIFF(SECOND, MIN(COALESCE(r.next_run_at_utc, j.created_at_utc)), @now) AS BIGINT)
        FROM {{schema}}.runtimes r
        JOIN {{schema}}.jobs j ON j.id = r.job_id
        WHERE
            (
                r.status_code = 10 /* JobStatusCode.Ready */
                OR (r.status_code = 20 /* JobStatusCode.Suspended */ AND r.next_run_at_utc IS NOT NULL)
            )
            -- Dueness is already this counter's own filter, so the Suspended half needs nothing extra.
            AND COALESCE(r.next_run_at_utc, j.created_at_utc) <= @due_now
            AND (@p_namespace_name IS NULL OR r.namespace_id = @ns_id)
    ) AS oldest_ready_age_seconds,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            AND (@p_namespace_name IS NULL OR r.namespace_id = @ns_id)
    ) AS executing_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code = 200 /* JobStatusCode.Failed */
            AND (@p_namespace_name IS NULL OR r.namespace_id = @ns_id)
    ) AS failed_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.alerts a
        WHERE
            a.resolved_at_utc IS NULL
            AND (@p_namespace_name IS NULL OR a.namespace_id = @ns_id)
    ) AS unresolved_alert_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.alerts a
        WHERE
            a.resolved_at_utc IS NULL
            AND a.severity_code = 40 /* AlertSeverityCode.Critical */
            AND (@p_namespace_name IS NULL OR a.namespace_id = @ns_id)
    ) AS unresolved_critical_alert_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.workers w
        WHERE
            w.status_code = 200 /* WorkerStatusCode.Dead */
            AND (@p_namespace_name IS NULL OR w.namespace_id = @ns_id)
    ) AS dead_worker_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.workers w
        WHERE
            w.status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
            AND w.last_seen_at_utc < DATEADD(SECOND, -@p_stale_after_seconds, @now)
            AND (@p_namespace_name IS NULL OR w.namespace_id = @ns_id)
    ) AS stale_worker_count,
    (
        SELECT COUNT_BIG(*)
        FROM {{schema}}.schedules s
        WHERE
            s.next_run_at_utc IS NOT NULL
            AND s.status_code = 10 /* ScheduleStatusCode.Active */
            AND s.next_run_at_utc <= DATEADD(SECOND, @p_due_soon_seconds, @now)
            AND (@p_namespace_name IS NULL OR s.namespace_id = @ns_id)
    ) AS due_soon_schedule_count,
    CASE
        WHEN @p_include_slow_counts IS NOT NULL THEN
            (
                SELECT COUNT_BIG(*)
                FROM {{schema}}.jobs j
                WHERE (@p_namespace_name IS NULL OR j.namespace_id = @ns_id)
            )
    END AS job_count,
    CASE
        WHEN @p_include_slow_counts IS NOT NULL THEN
            (
                SELECT COUNT_BIG(*)
                FROM {{schema}}.jobs j
                JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
                WHERE
                    jd.name LIKE 'sys.%'
                    AND (@p_namespace_name IS NULL OR j.namespace_id = @ns_id)
            )
    END AS system_job_count,
    (
        SELECT COALESCE(SUM(CAST(w.max_concurrency AS BIGINT)), 0)
        FROM {{schema}}.workers w
        WHERE
            w.status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
            AND w.last_seen_at_utc >= DATEADD(SECOND, -@p_stale_after_seconds, @now)
            AND (@p_namespace_name IS NULL OR w.namespace_id = @ns_id)
    ) AS executor_capacity,
    (
        SELECT CAST(DATEDIFF(SECOND, MIN(s.next_run_at_utc), @now) AS BIGINT)
        FROM {{schema}}.schedules s
        WHERE
            s.next_run_at_utc IS NOT NULL
            AND s.status_code = 10 /* ScheduleStatusCode.Active */
            AND s.next_run_at_utc <= @due_now
            AND (@p_namespace_name IS NULL OR s.namespace_id = @ns_id)
    ) AS schedule_lag_seconds
OPTION (RECOMPILE);
