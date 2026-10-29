DROP TABLE IF EXISTS temp._ps_target;

CREATE TEMP TABLE _ps_target AS
SELECT js.schedule_id
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.schedule_id = @p_schedule_id
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

-- Audited against the slot, or against the definition when the schedule has none.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details)
SELECT
    100 /* EventCode.SchedulePaused */,
    {{now}},
    js.namespace_id,
    CASE WHEN js.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(js.job_id, js.definition_id),
    js.definition_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._ps_target t
JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
LEFT JOIN {{schema}}.jobs j ON j.job_id = js.job_id
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = js.job_id
WHERE COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */;

UPDATE {{schema}}.schedules
SET
    schedule_status_code = 30 /* ScheduleStatusCode.Paused */,
    paused_until_utc = @p_paused_until_utc,
    pause_message = @p_pause_message,
    modified_at_utc = {{now}},
    version = version + 1
WHERE schedule_id IN (SELECT t.schedule_id FROM temp._ps_target t);

UPDATE {{schema}}.schedules_state
SET
    next_evaluation_at_utc = @p_paused_until_utc,
    version = version + 1,
    modified_at_utc = {{now}}
WHERE schedule_id IN (SELECT t.schedule_id FROM temp._ps_target t);


SELECT
    CASE
        WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT @p_schedule_id AS qid) q
LEFT JOIN temp._ps_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
