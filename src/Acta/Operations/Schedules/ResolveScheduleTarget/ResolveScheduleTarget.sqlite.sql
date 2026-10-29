-- The schedule a path names, by the definition's key and then the schedule's; job_id is its slot, NULL for a PerOccurrence
-- definition.
SELECT
    t.schedule_id,
    t.job_id,
    t.definition_id,
    n.namespace_id,
    t.schedule_name,
    t.expression,
    t.time_zone_id,
    t.misfire_strategy_code AS misfire_strategy,
    t.expression_kind_code AS expression_kind,
    st.next_occurrence_at_utc,
    t.schedule_status_code AS status,
    t.paused_until_utc,
    t.version,
    t.max_outstanding,
    st.outstanding_count,
    st.version AS state_version,
    t.interval_anchor_at_utc,
    t.calendar_origin_code AS calendar_origin,
    t.calendar_version,
    t.availability_calendar_name,
    t.availability_calendar_origin_code AS availability_origin,
    t.availability_calendar_version AS availability_version
FROM {{schema}}.namespaces n
INNER JOIN {{schema}}.definitions d ON d.namespace_id = n.namespace_id AND d.definition_name = @p_job_name
INNER JOIN {{schema}}.schedules t ON t.definition_id = d.definition_id AND t.schedule_name = @p_schedule_name
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = t.schedule_id
WHERE
    n.namespace_name = @p_namespace_name
    AND t.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;
