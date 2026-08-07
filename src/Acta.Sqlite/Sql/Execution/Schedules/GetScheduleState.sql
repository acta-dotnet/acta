SELECT
    t.definition_id,
    t.name,
    t.next_run_at_utc,
    t.status_code,
    t.paused_until_utc
FROM {{schema}}.schedules t
WHERE
    t.namespace_id = @p_namespace_id
    AND t.status_code <> 230 /* ScheduleStatusCode.Orphaned */;
