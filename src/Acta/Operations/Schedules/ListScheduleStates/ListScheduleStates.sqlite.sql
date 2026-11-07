-- Scans the namespace's schedules on the config table: a paused or exhausted schedule can carry a NULL
-- cursor, which the filtered cursor index leaves out, and this read runs once per namespace at a deploy.
SELECT
    t.definition_id,
    t.schedule_name,
    st.next_occurrence_at_utc,
    r.job_status_code AS slot_status,
    t.expression_kind_code AS expression_kind,
    t.interval_anchor_at_utc,
    t.timing_fingerprint
FROM {{schema}}.schedules t
INNER JOIN {{schema}}.definitions d ON d.definition_id = t.definition_id
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = t.job_id
WHERE
    t.namespace_id = @p_namespace_id
    AND t.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;
