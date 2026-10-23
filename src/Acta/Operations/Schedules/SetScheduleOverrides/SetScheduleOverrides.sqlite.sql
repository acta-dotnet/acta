DROP TABLE IF EXISTS temp._sso_target;

CREATE TEMP TABLE _sso_target AS
SELECT
    js.schedule_name,
    CASE
        WHEN js.version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 1 /* ControlAction.Applied */
    END AS action
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
    103 /* EventCode.ScheduleOverridesUpdated */,
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
    AND EXISTS (SELECT 1 FROM temp._sso_target WHERE action = 1 /* ControlAction.Applied */);

UPDATE {{schema}}.schedules
SET
    expression_override = @p_expression,
    time_zone_id_override = @p_time_zone_id,
    change_message = @p_change_message,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM temp._sso_target t WHERE t.action = 1 /* ControlAction.Applied */);

UPDATE {{schema}}.schedules_state
SET
    next_occurrence_at_utc = @p_next_occurrence_at_utc,
    modified_at_utc = {{now}}
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM temp._sso_target t WHERE t.action = 1 /* ControlAction.Applied */);

-- Paused with a next run is an operator's or handler's hold and is left alone; Paused with none only
-- means no schedule offered a run (ScheduleWalker, "held").
UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = @p_job_next_run_at_utc,
    job_status_code = CASE WHEN @p_job_next_run_at_utc IS NULL
        THEN 30 /* JobStatusCode.Paused */
        ELSE 10 /* JobStatusCode.Ready */ END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (job_status_code = 10 /* JobStatusCode.Ready */ OR (job_status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL))
    AND EXISTS (SELECT 1 FROM temp._sso_target WHERE action = 1 /* ControlAction.Applied */);

SELECT
    CASE WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */ ELSE t.action END AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._sso_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name;
