-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes
-- before any row lock (docs/sql-internals.md, "Schedule lock"), so the reads after it see a committed retire or
-- registration; a slot's jobs_state row then locks before its schedules row, the order a fire's completion takes.
DROP TABLE IF EXISTS pg_temp._resume_schedule_lock;
DROP TABLE IF EXISTS pg_temp._resume_schedule_slot;
DROP TABLE IF EXISTS pg_temp._resume_schedule_target;
CREATE TEMP TABLE _resume_schedule_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_schedule_slot (job_id BIGINT NOT NULL, execution_number INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_schedule_target (schedule_id INT NOT NULL) ON COMMIT DROP;

INSERT INTO _resume_schedule_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), js.namespace_id)
    FROM {{schema}}.schedules js
    WHERE js.schedule_id = @p_schedule_id
) l;

INSERT INTO _resume_schedule_slot (job_id, execution_number)
SELECT r.job_id, r.execution_number
FROM {{schema}}.jobs_state r
WHERE r.job_id = (SELECT js.job_id FROM {{schema}}.schedules js WHERE js.schedule_id = @p_schedule_id)
FOR UPDATE;

INSERT INTO _resume_schedule_target (schedule_id)
SELECT js.schedule_id
FROM {{schema}}.schedules js
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
WHERE
    js.schedule_id = @p_schedule_id
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
WHERE schedule_id IN (SELECT t.schedule_id FROM _resume_schedule_target t);

UPDATE {{schema}}.schedules_state
SET
    -- The remembered cursor stays; the admitter applies the misfire policy to it at its next pass.
    next_evaluation_at_utc = now(),
    evaluation_status_code = 60 /* EvaluationStatusCode.Resumed */,
    version = version + 1,
    modified_at_utc = now()
WHERE schedule_id IN (SELECT t.schedule_id FROM _resume_schedule_target t);

-- Audited against the slot, or against the definition when the schedule has none.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    101 /* EventCode.ScheduleResumed */, now(), js.namespace_id,
    CASE WHEN js.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(js.job_id, js.definition_id),
    js.definition_id, COALESCE(j.lineage_root_job_id, j.job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _resume_schedule_target t
INNER JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = js.definition_id
LEFT JOIN {{schema}}.jobs j ON j.job_id = js.job_id
LEFT JOIN _resume_schedule_slot s ON s.job_id = js.job_id
WHERE COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */;

SELECT
    CASE WHEN t.schedule_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END::SMALLINT AS action,
    js.schedule_status_code AS status,
    js.paused_until_utc,
    st.next_occurrence_at_utc,
    js.version AS version
FROM (SELECT 1) one
LEFT JOIN _resume_schedule_target t ON true
LEFT JOIN {{schema}}.schedules js ON js.schedule_id = t.schedule_id
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = js.schedule_id;
