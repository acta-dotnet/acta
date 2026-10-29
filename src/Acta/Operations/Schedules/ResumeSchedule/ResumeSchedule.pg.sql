-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes
-- before any row lock (docs/sql-internals.md, "Schedule lock"), so the reads after it see a committed retire or
-- registration; the slot's jobs_state row then locks before its schedules row, the order a fire's completion takes.
DROP TABLE IF EXISTS pg_temp._resume_schedule_lock;
DROP TABLE IF EXISTS pg_temp._resume_schedule_slot;
DROP TABLE IF EXISTS pg_temp._resume_schedule_target;
CREATE TEMP TABLE _resume_schedule_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_schedule_slot (execution_number INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_schedule_target (schedule_name VARCHAR(128) NOT NULL) ON COMMIT DROP;

INSERT INTO _resume_schedule_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), j.namespace_id)
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id
) l;

INSERT INTO _resume_schedule_slot (execution_number)
SELECT r.execution_number
FROM {{schema}}.jobs_state r
WHERE r.job_id = @p_job_id
FOR UPDATE;

INSERT INTO _resume_schedule_target (schedule_name)
SELECT js.schedule_name
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
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = now(),
    version = version + 1
WHERE
    job_id = @p_job_id
    AND schedule_name IN (SELECT t.schedule_name FROM _resume_schedule_target t);

UPDATE {{schema}}.schedules_state
SET
    -- The remembered cursor stays; the admitter applies the misfire policy to it at its next pass.
    next_evaluation_at_utc = now(),
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    version = version + 1,
    modified_at_utc = now()
WHERE
    schedule_id IN (
        SELECT js.schedule_id
        FROM {{schema}}.schedules js
        WHERE js.job_id = @p_job_id AND js.schedule_name IN (SELECT t.schedule_name FROM _resume_schedule_target t)
    );


INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    101 /* EventCode.ScheduleResumed */, now(), j.namespace_id,
    50 /* EntityCode.Job */, j.job_id,
    j.definition_id, COALESCE(j.lineage_root_job_id, j.job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM {{schema}}.jobs j
CROSS JOIN _resume_schedule_slot s
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND EXISTS (SELECT 1 FROM _resume_schedule_target);

SELECT
    CASE WHEN t.schedule_name IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END::SMALLINT AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT 1) one
LEFT JOIN _resume_schedule_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.job_id = @p_job_id AND js.schedule_name = t.schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
