DROP TABLE IF EXISTS temp._tsn_target;

CREATE TEMP TABLE _tsn_target AS
SELECT
    js.schedule_name,
    CASE
        WHEN js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN r.job_status_code = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        -- A held job (Paused with a next run; ScheduleWalker, "held") runs only once it is resumed.
        WHEN r.job_status_code = 30 /* JobStatusCode.Paused */ AND r.next_run_at_utc IS NOT NULL THEN 3 /* ControlAction.Rejected */
        WHEN r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END AS action
FROM {{schema}}.schedules js
JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
JOIN {{schema}}.jobs_state r ON r.job_id = js.job_id
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
    104 /* EventCode.ScheduleTriggered */,
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
    AND EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* ControlAction.Applied */);

UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = {{now}},
    job_status_code = 10 /* JobStatusCode.Ready */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (
        job_status_code IN (20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */)
        OR (job_status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL)
    )
    AND EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* ControlAction.Applied */);

SELECT
    CASE WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */ ELSE t.action END AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._tsn_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name;
