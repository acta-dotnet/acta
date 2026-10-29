DROP TABLE IF EXISTS temp._rs_target;

CREATE TEMP TABLE _rs_target AS
SELECT js.schedule_name
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.job_id = @p_job_id
    AND js.schedule_name = @p_schedule_name
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

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
    101 /* EventCode.ScheduleResumed */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND EXISTS (SELECT 1 FROM temp._rs_target);

UPDATE {{schema}}.schedules
SET
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM temp._rs_target t);

UPDATE {{schema}}.schedules_state
SET
    -- The remembered cursor stays; the admitter applies the misfire policy to it at its next pass.
    next_evaluation_at_utc = {{now}},
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    version = version + 1,
    modified_at_utc = {{now}}
WHERE
    schedule_id IN (
        SELECT js.schedule_id
        FROM {{schema}}.schedules js
        WHERE js.job_id = @p_job_id AND js.schedule_name IN (SELECT t.schedule_name FROM temp._rs_target t)
    );


SELECT
    CASE
        WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._rs_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
