-- One batch, one implicit transaction; only the final SELECT returns rows. The slot's runtimes row is
-- locked before the schedules row: register_schedules writes runtimes then schedules, so every
-- writer of both takes runtimes first. A stale version is Rejected and changes nothing.
DROP TABLE IF EXISTS pg_temp._set_schedule_overrides_slot;
DROP TABLE IF EXISTS pg_temp._set_schedule_overrides_target;
CREATE TEMP TABLE _set_schedule_overrides_slot (execution_number INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _set_schedule_overrides_target (schedule_id BIGINT NOT NULL, version INT NOT NULL) ON COMMIT DROP;

INSERT INTO _set_schedule_overrides_slot (execution_number)
SELECT r.execution_number
FROM {{schema}}.runtimes r
WHERE r.job_id = @p_job_id
FOR UPDATE;

INSERT INTO _set_schedule_overrides_target (schedule_id, version)
SELECT js.id, js.version
FROM {{schema}}.schedules js
WHERE
    js.job_id = @p_job_id
    AND js.name = @p_name
    AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
FOR UPDATE;

UPDATE {{schema}}.schedules
SET
    expression_override = @p_expression,
    time_zone_id_override = @p_time_zone_id,
    next_run_at_utc = @p_schedule_next_run_at_utc,
    reason_message = @p_reason_message,
    modified_at_utc = now(),
    version = version + 1
WHERE id IN (SELECT t.schedule_id FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version);

-- Paused with a next run is an operator's or handler's hold and is left alone; Paused with none only
-- means no schedule offered a run (ScheduleWalker, "held").
UPDATE {{schema}}.runtimes
SET
    next_run_at_utc = @p_job_next_run_at_utc,
    status_code = CASE WHEN @p_job_next_run_at_utc IS NULL
        THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version)
    AND (status_code = 10 /* JobStatusCode.Ready */ OR (status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL));

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, reason_message)
SELECT
    103 /* EventCode.ScheduleOverridesUpdated */, now(), j.namespace_id, @p_actor_code, @p_actor, j.id, j.job_ref,
    s.execution_number, COALESCE(j.lineage_root_id, j.id), j.definition_id, @p_change_summary
FROM {{schema}}.jobs j
CROSS JOIN _set_schedule_overrides_slot s
WHERE
    j.id = @p_job_id
    AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND EXISTS (SELECT 1 FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version);

SELECT
    CASE
        WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.version <> @p_expected_version THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    js.status_code AS status_code,
    js.paused_until_utc AS paused_until_utc,
    js.next_run_at_utc AS next_run_at_utc,
    js.version AS version
FROM (SELECT 1) one
LEFT JOIN _set_schedule_overrides_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.id = t.schedule_id;
