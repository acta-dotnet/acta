-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes
-- before any row lock (docs/sql-internals.md, "Schedule lock"), then the slot's jobs_state row, then its schedules row,
-- the order an admission takes them in: a manual firing is an admission with no contributors.
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_lock;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_slot;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_target;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_action;
CREATE TEMP TABLE _trigger_schedule_now_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_slot (
    job_status_code SMALLINT NOT NULL,
    firing_status_code SMALLINT,
    execution_number INT NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_target (schedule_id INT NOT NULL, schedule_status_code SMALLINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_action (action SMALLINT NOT NULL) ON COMMIT DROP;

INSERT INTO _trigger_schedule_now_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), j.namespace_id)
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id
) l;

INSERT INTO _trigger_schedule_now_slot (job_status_code, firing_status_code, execution_number)
SELECT r.job_status_code, cp.checkpoint_status_code, r.execution_number
FROM {{schema}}.jobs_state r
LEFT JOIN {{schema}}.checkpoints cp
    ON cp.job_id = r.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE r.job_id = @p_job_id
FOR UPDATE OF r;

INSERT INTO _trigger_schedule_now_target (schedule_id, schedule_status_code)
SELECT js.schedule_id, js.schedule_status_code
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.schedule_id = @p_schedule_id
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
FOR UPDATE OF js;

-- Only an Idle slot takes a manual firing: a busy one is Rejected rather than queued behind, and a held, cancelled, or
-- finished one needs its operator verb first. A paused schedule is Rejected too.
INSERT INTO _trigger_schedule_now_action (action)
SELECT
    CASE
        WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN s.job_status_code = 40 /* JobStatusCode.Idle */ AND s.firing_status_code IS DISTINCT FROM 10 /* CheckpointStatusCode.Pending */
            THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END
FROM (SELECT 1) one
LEFT JOIN _trigger_schedule_now_target t ON true
LEFT JOIN _trigger_schedule_now_slot s ON true;

UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = now(),
    job_status_code = 10 /* JobStatusCode.Ready */,
    failure_count = 0,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */);

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, convert_to(@p_envelope, 'UTF8'), now(), now(), 0
WHERE EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = EXCLUDED.checkpoint_status_code,
    value_format_id = EXCLUDED.value_format_id,
    value = EXCLUDED.value,
    modified_at_utc = now(),
    version = {{schema}}.checkpoints.version + 1;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    104 /* EventCode.ScheduleTriggered */, now(), j.namespace_id,
    50 /* EntityCode.Job */, j.job_id,
    j.definition_id, COALESCE(j.lineage_root_job_id, j.job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM {{schema}}.jobs j
CROSS JOIN _trigger_schedule_now_slot s
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */);

SELECT
    a.action AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM _trigger_schedule_now_action a
LEFT JOIN _trigger_schedule_now_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
