/* The two backlog counters below admit what claim_jobs claims. Ready keeps its gauge meaning and
   counts whether or not it is due; a Suspended row counts only once its wait has actually expired, or
   a queue of long approval waits would read as backlog that no worker is behind on. */
/* Not shared text with the claim, so the two are must-stay-in-sync twins; change one, change the other. */
SELECT
    (
        SELECT COUNT(*)
        FROM {{schema}}.runtimes r
        WHERE
            -- ix_runtimes_claim_ready's filter restated whole, so both counters read the claim index.
            r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND r.next_run_at_utc IS NOT NULL
            AND (r.status_code = 10 /* JobStatusCode.Ready */ OR r.next_run_at_utc <= {{now}})
            AND (@p_namespace_name IS NULL OR r.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS ready_count,
    (
        SELECT ({{now}} - MIN(r.next_run_at_utc)) / 1000
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND r.next_run_at_utc IS NOT NULL
            AND r.next_run_at_utc <= {{now}}
            AND (@p_namespace_name IS NULL OR r.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS oldest_ready_age_seconds,
    (
        SELECT COUNT(*)
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            -- ix_runtimes_worker_inflight's filter restated (ck_runtimes_inflight_leased holds it for every Executing
            -- row), so the count reads the in-flight rows instead of scanning runtimes.
            AND r.leased_by_worker_id IS NOT NULL
            AND (@p_namespace_name IS NULL OR r.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS executing_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code = 200 /* JobStatusCode.Failed */
            AND (@p_namespace_name IS NULL OR r.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS failed_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.alerts a
        WHERE
            a.resolved_at_utc IS NULL
            AND (@p_namespace_name IS NULL OR a.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS unresolved_alert_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.alerts a
        WHERE
            a.resolved_at_utc IS NULL
            AND a.severity_code = 40 /* AlertSeverityCode.Critical */
            AND (@p_namespace_name IS NULL OR a.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS unresolved_critical_alert_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.workers w
        WHERE
            w.status_code = 200 /* WorkerStatusCode.Dead */
            AND (@p_namespace_name IS NULL OR w.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS dead_worker_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.workers w
        WHERE
            w.status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
            AND w.last_seen_at_utc < {{now}} - (@p_stale_after_seconds) * 1000
            AND (@p_namespace_name IS NULL OR w.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS stale_worker_count,
    (
        SELECT COUNT(*)
        FROM {{schema}}.schedules s
        WHERE
            s.next_run_at_utc IS NOT NULL
            AND s.status_code = 10 /* ScheduleStatusCode.Active */
            AND s.next_run_at_utc <= {{now}} + (@p_due_soon_seconds) * 1000
            AND (@p_namespace_name IS NULL OR s.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS due_soon_schedule_count,
    (
        SELECT COALESCE(SUM(w.max_concurrency), 0)
        FROM {{schema}}.workers w
        WHERE
            w.status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
            AND w.last_seen_at_utc >= {{now}} - (@p_stale_after_seconds) * 1000
            AND (@p_namespace_name IS NULL OR w.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS executor_capacity,
    (
        SELECT ({{now}} - MIN(s.next_run_at_utc)) / 1000
        FROM {{schema}}.schedules s
        WHERE
            s.next_run_at_utc IS NOT NULL
            AND s.status_code = 10 /* ScheduleStatusCode.Active */
            AND s.next_run_at_utc <= {{now}}
            AND (@p_namespace_name IS NULL OR s.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name))
    ) AS schedule_lag_seconds;
