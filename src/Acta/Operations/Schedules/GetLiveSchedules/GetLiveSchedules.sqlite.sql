SELECT
    t.schedule_name,
    t.expression,
    t.time_zone_id,
    t.misfire_strategy_code AS misfire_strategy,
    t.expression_kind_code AS expression_kind,
    st.next_occurrence_at_utc,
    t.schedule_status_code AS status,
    t.paused_until_utc,
    t.version
FROM {{schema}}.schedules t
INNER JOIN {{schema}}.definitions d ON d.definition_id = t.definition_id
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = t.schedule_id
WHERE
    t.job_id = @p_job_id
    AND t.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
ORDER BY t.schedule_name;
