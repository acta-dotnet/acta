SELECT
    t.schedule_name,
    t.expression_effective AS expression,
    t.time_zone_id_effective AS time_zone_id,
    t.misfire_strategy_code AS misfire_strategy,
    t.expression_kind_code AS expression_kind,
    st.next_occurrence_at_utc,
    t.schedule_status_code AS status,
    t.paused_until_utc,
    t.expression AS base_expression,
    t.time_zone_id AS base_time_zone,
    t.version
FROM {{schema}}.schedules t
INNER JOIN {{schema}}.definitions d ON d.definition_id = t.definition_id
INNER JOIN {{schema}}.schedules_state st ON st.job_id = t.job_id AND st.schedule_name = t.schedule_name
WHERE
    t.job_id = @p_job_id
    AND t.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
ORDER BY t.schedule_name;
