SELECT
    s.job_id,
    s.definition_id,
    ns.name AS job_namespace,
    jd.name AS job_name,
    s.name AS schedule_name,
    s.origin_code AS origin,
    s.expression_kind_code AS expression_kind,
    s.expression_effective AS expression,
    s.time_zone_id_effective AS time_zone_id,
    s.misfire_strategy_code AS misfire_strategy,
    s.next_run_at_utc,
    s.last_occurrence_at_utc AS last_run_at_utc,
    s.status_code AS status,
    s.paused_until_utc,
    s.created_at_utc,
    s.modified_at_utc,
    s.version,
    sj.job_ref
FROM {{schema}}.schedules s
JOIN {{schema}}.namespaces ns ON ns.id = s.namespace_id
JOIN {{schema}}.definitions jd ON jd.id = s.definition_id
LEFT JOIN {{schema}}.jobs sj ON sj.id = s.job_id
WHERE
    s.next_run_at_utc IS NOT NULL
    AND (@p_live_only IS NULL OR s.status_code <> 230 /* ScheduleStatusCode.Orphaned */)
    AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
    AND (@p_job_name IS NULL OR jd.name = @p_job_name)
    AND (@p_origin_code IS NULL OR s.origin_code = @p_origin_code)
    AND (
        @p_cursor_next_run_at_utc IS NULL
        OR s.next_run_at_utc > @p_cursor_next_run_at_utc
        OR (s.next_run_at_utc = @p_cursor_next_run_at_utc AND s.job_id > @p_cursor_id)
        OR (s.next_run_at_utc = @p_cursor_next_run_at_utc AND s.job_id = @p_cursor_id AND s.name > @p_cursor_schedule_name)
    )
ORDER BY s.next_run_at_utc ASC, s.job_id ASC, s.name ASC
LIMIT @p_take;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM {{schema}}.schedules s
    JOIN {{schema}}.namespaces ns ON ns.id = s.namespace_id
    JOIN {{schema}}.definitions jd ON jd.id = s.definition_id
    WHERE
        s.next_run_at_utc IS NOT NULL
        AND (@p_live_only IS NULL OR s.status_code <> 230 /* ScheduleStatusCode.Orphaned */)
        AND (@p_namespace_name IS NULL OR ns.name = @p_namespace_name)
        AND (@p_job_name IS NULL OR jd.name = @p_job_name)
        AND (@p_origin_code IS NULL OR s.origin_code = @p_origin_code)
) END;
