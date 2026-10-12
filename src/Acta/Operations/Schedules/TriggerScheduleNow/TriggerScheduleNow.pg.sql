-- One batch, one implicit transaction; only the final SELECT returns rows. The slot's runtimes row is
-- locked before the schedules row (register_schedules writes runtimes then schedules); the schedules
-- row is guard-only, since a manual trigger moves only the slot's cursor.
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_slot;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_target;
DROP TABLE IF EXISTS pg_temp._trigger_schedule_now_action;
CREATE TEMP TABLE _trigger_schedule_now_slot (
    status_code SMALLINT NOT NULL,
    next_run_at_utc TIMESTAMPTZ,
    execution_number INT NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_target (schedule_id BIGINT NOT NULL, status_code SMALLINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _trigger_schedule_now_action (action SMALLINT NOT NULL) ON COMMIT DROP;

INSERT INTO _trigger_schedule_now_slot (status_code, next_run_at_utc, execution_number)
SELECT r.status_code, r.next_run_at_utc, r.execution_number
FROM {{schema}}.runtimes r
WHERE r.job_id = @p_job_id
FOR UPDATE;

INSERT INTO _trigger_schedule_now_target (schedule_id, status_code)
SELECT js.id, js.status_code
FROM {{schema}}.schedules js
WHERE
    js.job_id = @p_job_id
    AND js.name = @p_name
    AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
FOR UPDATE;

-- A paused schedule, an in-flight or finished slot, and a held slot (Paused with a next run; ScheduleWalker,
-- "held") are Rejected: the held one runs only once it is resumed.
INSERT INTO _trigger_schedule_now_action (action)
SELECT
    CASE
        WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.status_code = 30 /* ScheduleStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        WHEN s.status_code = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        WHEN s.status_code = 30 /* JobStatusCode.Paused */ AND s.next_run_at_utc IS NOT NULL THEN 3 /* ControlAction.Rejected */
        WHEN s.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END
FROM (SELECT 1) one
LEFT JOIN _trigger_schedule_now_target t ON true
LEFT JOIN _trigger_schedule_now_slot s ON true;

UPDATE {{schema}}.runtimes
SET
    next_run_at_utc = now(),
    status_code = 10 /* JobStatusCode.Ready */,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */)
    AND (
        status_code IN (20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */)
        OR (status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL)
    );

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, reason_message)
SELECT
    104 /* EventCode.ScheduleTriggered */, now(), j.namespace_id, @p_actor_code, @p_actor, j.id, j.job_ref,
    s.execution_number, COALESCE(j.lineage_root_id, j.id), j.definition_id, @p_reason_message
FROM {{schema}}.jobs j
CROSS JOIN _trigger_schedule_now_slot s
WHERE
    j.id = @p_job_id
    AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND EXISTS (SELECT 1 FROM _trigger_schedule_now_action a WHERE a.action = 1 /* ControlAction.Applied */);

SELECT
    a.action AS action,
    js.status_code AS status_code,
    js.paused_until_utc AS paused_until_utc,
    js.next_run_at_utc AS next_run_at_utc,
    js.version AS version
FROM _trigger_schedule_now_action a
LEFT JOIN _trigger_schedule_now_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.id = t.schedule_id;
