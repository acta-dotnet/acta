-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes before
-- any row lock (docs/sql-execution-policy.md, "Schedule lock"), then the slot's jobs_state row before its schedules row, the
-- order a fire's completion takes; the schedules row is guard-only, since a manual trigger moves only the slot's cursor.
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_lock;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_slot;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_target;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_action;
CREATE TEMP TABLE _trigger_schedule_now_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_slot (
    job_status_code SMALLINT NOT NULL,
    next_run_at_utc TIMESTAMPTZ,
    execution_number INT NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_target (schedule_name VARCHAR(128) NOT NULL, schedule_status_code SMALLINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_action (action SMALLINT NOT NULL) ON COMMIT DROP;

INSERT INTO _trigger_schedule_now_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), j.namespace_id)
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id
) l;

INSERT INTO _trigger_schedule_now_slot (job_status_code, next_run_at_utc, execution_number)
SELECT r.job_status_code, r.next_run_at_utc, r.execution_number
FROM {{schema}}.jobs_state r
WHERE r.job_id = @p_job_id
FOR UPDATE;

INSERT INTO _trigger_schedule_now_target (schedule_name, schedule_status_code)
SELECT js.schedule_name, js.schedule_status_code
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.job_id = @p_job_id
    AND js.schedule_name = @p_schedule_name
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
FOR UPDATE OF js;

-- A paused schedule, an in-flight or finished slot, and a held slot (Paused with a next run; ScheduleWalker,
-- "held") are Rejected: the held one runs only once it is resumed.
INSERT INTO _trigger_schedule_now_action (action)
SELECT
    CASE
        WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN s.job_status_code = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        WHEN s.job_status_code = 30 /* JobStatusCode.Paused */ AND s.next_run_at_utc IS NOT NULL THEN 3 /* ControlAction.Rejected */
        WHEN s.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END
FROM (SELECT 1) one
LEFT JOIN _trigger_schedule_now_target t ON true
LEFT JOIN _trigger_schedule_now_slot s ON true;

UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = now(),
    job_status_code = 10 /* JobStatusCode.Ready */,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */)
    AND (
        job_status_code IN (20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */)
        OR (job_status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL)
    );

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
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name;
