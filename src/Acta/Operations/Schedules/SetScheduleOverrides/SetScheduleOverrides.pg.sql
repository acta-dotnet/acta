-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes before
-- any row lock (docs/sql-internals.md, "Schedule lock"), then the slot's jobs_state row before its schedules row, the
-- order a fire's completion takes; the reads see a committed retire. A stale version is a VersionConflict and changes nothing.
DROP TABLE IF EXISTS pg_temp._set_schedule_overrides_lock;
DROP TABLE IF EXISTS pg_temp._set_schedule_overrides_slot;
DROP TABLE IF EXISTS pg_temp._set_schedule_overrides_target;
CREATE TEMP TABLE _set_schedule_overrides_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _set_schedule_overrides_slot (execution_number INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _set_schedule_overrides_target (schedule_name VARCHAR(128) NOT NULL, version INT NOT NULL) ON COMMIT DROP;

INSERT INTO _set_schedule_overrides_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), j.namespace_id)
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id
) l;

INSERT INTO _set_schedule_overrides_slot (execution_number)
SELECT r.execution_number
FROM {{schema}}.jobs_state r
WHERE r.job_id = @p_job_id
FOR UPDATE;

INSERT INTO _set_schedule_overrides_target (schedule_name, version)
SELECT js.schedule_name, js.version
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.job_id = @p_job_id
    AND js.schedule_name = @p_schedule_name
    AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
FOR UPDATE OF js;

UPDATE {{schema}}.schedules
SET
    expression_override = @p_expression,
    time_zone_id_override = @p_time_zone_id,
    change_message = @p_change_message,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version);

UPDATE {{schema}}.schedules_state
SET
    next_occurrence_at_utc = @p_next_occurrence_at_utc,
    modified_at_utc = now()
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version);

-- Paused with a next run is an operator's or handler's hold and is left alone; Paused with none only
-- means no schedule offered a run (ScheduleWalker, "held").
UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = @p_job_next_run_at_utc,
    job_status_code = CASE WHEN @p_job_next_run_at_utc IS NULL
        THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version)
    AND (job_status_code = 10 /* JobStatusCode.Ready */ OR (job_status_code = 30 /* JobStatusCode.Paused */ AND next_run_at_utc IS NULL));

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    103 /* EventCode.ScheduleOverridesUpdated */, now(), j.namespace_id,
    50 /* EntityCode.Job */, j.job_id,
    j.definition_id, COALESCE(j.lineage_root_job_id, j.job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM {{schema}}.jobs j
CROSS JOIN _set_schedule_overrides_slot s
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND EXISTS (SELECT 1 FROM _set_schedule_overrides_target t WHERE t.version = @p_expected_version);

SELECT
    CASE
        WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT 1) one
LEFT JOIN _set_schedule_overrides_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name;
