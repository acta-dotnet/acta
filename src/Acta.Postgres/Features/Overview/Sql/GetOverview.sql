SELECT
  (SELECT COUNT(*)
     FROM {{schema}}.runtimes r
     JOIN {{schema}}.namespaces ns ON ns.id = r.namespace_id
    WHERE r.status_code = 10 /* JobStatusCode.Ready */
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS ready_count,
  (SELECT FLOOR(EXTRACT(EPOCH FROM now() - MIN(COALESCE(r.next_run_at_utc, j.created_at_utc))))::bigint
     FROM {{schema}}.runtimes r
     JOIN {{schema}}.jobs j ON j.id = r.job_id
     JOIN {{schema}}.namespaces ns ON ns.id = r.namespace_id
    WHERE r.status_code = 10 /* JobStatusCode.Ready */
      AND COALESCE(r.next_run_at_utc, j.created_at_utc) <= now()
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS oldest_ready_age_seconds,
  (SELECT COUNT(*)
     FROM {{schema}}.runtimes r
     JOIN {{schema}}.namespaces ns ON ns.id = r.namespace_id
    WHERE r.status_code = 50 /* JobStatusCode.Executing */
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS executing_count,
  (SELECT COUNT(*)
     FROM {{schema}}.runtimes r
     JOIN {{schema}}.namespaces ns ON ns.id = r.namespace_id
    WHERE r.status_code = 200 /* JobStatusCode.Failed */
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS failed_count,
  (SELECT COUNT(*)
     FROM {{schema}}.alerts a
     JOIN {{schema}}.namespaces ns ON ns.id = a.namespace_id
    WHERE a.resolved_at_utc IS NULL
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS unresolved_alert_count,
  (SELECT COUNT(*)
     FROM {{schema}}.alerts a
     JOIN {{schema}}.namespaces ns ON ns.id = a.namespace_id
    WHERE a.resolved_at_utc IS NULL
      AND a.severity_code = 40 /* AlertSeverityCode.Critical */
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS unresolved_critical_alert_count,
  (SELECT COUNT(*)
     FROM {{schema}}.workers w
     JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
    WHERE w.status_code = 200 /* WorkerStatusCode.Dead */
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS dead_worker_count,
  (SELECT COUNT(*)
     FROM {{schema}}.workers w
     JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
    WHERE w.status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
      AND w.last_seen_at_utc < now() - make_interval(secs => @p_stale_after_seconds)
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS stale_worker_count,
  (SELECT COUNT(*)
     FROM {{schema}}.schedules s
     JOIN {{schema}}.namespaces ns ON ns.id = s.namespace_id
    WHERE s.next_run_at_utc IS NOT NULL
      AND s.orphaned_at_utc IS NULL
      AND s.status_code = 10 /* ScheduleStatusCode.Active */
      AND s.next_run_at_utc <= now() + make_interval(secs => @p_due_soon_seconds)
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)) AS due_soon_schedule_count,
  CASE WHEN @p_include_slow_counts IS NOT NULL THEN
      (SELECT COUNT(*)
     FROM {{schema}}.jobs j
     JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
    WHERE (@p_namespace_name IS NULL OR ns.name = @p_namespace_name))
  END AS job_count,
  CASE WHEN @p_include_slow_counts IS NOT NULL THEN
      (SELECT COUNT(*)
     FROM {{schema}}.jobs j
     JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
     JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
    WHERE jd.name LIKE 'sys.%'
      AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name))
  END AS system_job_count;
