SELECT
    t.id,
    t.name,
    t.expression_effective AS expression,
    t.time_zone_id_effective AS time_zone_id,
    t.misfire_strategy_code AS misfire_strategy,
    t.expression_kind_code AS expression_kind,
    t.next_run_at_utc,
    t.status_code AS status,
    t.paused_until_utc,
    t.expression AS base_expression,
    t.time_zone_id AS base_time_zone,
    t.version
FROM {{schema}}.schedules t
WHERE
    t.job_id = @p_job_id
    AND t.status_code <> 230 /* ScheduleStatusCode.Orphaned */
ORDER BY t.name;
