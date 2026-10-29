DROP TABLE IF EXISTS temp._tsn_target;

-- Only an Idle slot takes a manual firing: a busy one is Rejected rather than queued behind, and a held, cancelled, or
-- finished one needs its operator verb first. A paused schedule is Rejected too. A manual firing is an admission with
-- no contributors.
CREATE TEMP TABLE _tsn_target AS
SELECT
    js.schedule_id,
    r.execution_number,
    CASE
        WHEN js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN r.job_status_code = 40 /* JobStatusCode.Idle */ AND COALESCE(cp.checkpoint_status_code, 0) <> 10 /* CheckpointStatusCode.Pending */
            THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action
FROM {{schema}}.schedules js
JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
JOIN {{schema}}.jobs_state r ON r.job_id = js.job_id
LEFT JOIN {{schema}}.checkpoints cp
    ON cp.job_id = r.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE
    js.schedule_id = @p_schedule_id
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = {{now}},
    job_status_code = 10 /* JobStatusCode.Ready */,
    failure_count = 0,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* ControlAction.Applied */);

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, CAST(@p_envelope AS BLOB), {{now}}, {{now}}, 0
WHERE EXISTS (SELECT 1 FROM temp._tsn_target WHERE action = 1 /* ControlAction.Applied */)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = excluded.checkpoint_status_code,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = version + 1;

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
    t.execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN temp._tsn_target t ON t.action = 1 /* ControlAction.Applied */
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

SELECT
    CASE WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE t.action END AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT 1) one
LEFT JOIN temp._tsn_target t ON 1 = 1
LEFT JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
