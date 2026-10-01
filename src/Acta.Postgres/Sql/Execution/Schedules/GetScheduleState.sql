SELECT
    t.definition_id,
    t.name,
    t.next_run_at_utc,
    t.status_code,
    t.paused_until_utc,
    r.status_code AS slot_status_code,
    r.next_run_at_utc AS slot_next_run_at_utc,
    t.expression_kind_code,
    t.expression_override,
    t.time_zone_id_override
FROM {{schema}}.schedules t
LEFT JOIN {{schema}}.runtimes r ON r.job_id = t.job_id
WHERE
    t.namespace_id = @p_namespace_id
    AND t.status_code <> 230 /* ScheduleStatusCode.Orphaned */;
