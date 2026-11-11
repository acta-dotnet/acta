-- Reads the namespace's schedules on the config table: a paused or exhausted schedule can carry a NULL cursor, which
-- the filtered cursor index leaves out. The read starts from the namespace's definitions and seeks each one's
-- schedules, since schedules has no index on the namespace and a read by namespace alone reads the table whole.
SELECT
    t.definition_id,
    t.schedule_name,
    st.next_occurrence_at_utc,
    r.job_status_code AS slot_status,
    t.expression_kind_code AS expression_kind,
    t.interval_anchor_at_utc,
    t.timing_fingerprint
FROM {{schema}}.definitions d
INNER JOIN {{schema}}.schedules t ON t.definition_id = d.definition_id
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = t.job_id
WHERE
    d.namespace_id = @p_namespace_id
    AND t.namespace_id = @p_namespace_id
    AND t.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;
