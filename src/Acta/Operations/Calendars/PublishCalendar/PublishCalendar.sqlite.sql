-- One owned transaction, SQLite's one writer, so no lock is taken; only the final SELECT returns rows. The plan was made
-- from a snapshot; every version it read is fenced here, and a moved one rejects the whole publication.
DROP TABLE IF EXISTS temp._pub_rows;
DROP TABLE IF EXISTS temp._pub_target;
DROP TABLE IF EXISTS temp._pub_valid;

CREATE TEMP TABLE _pub_rows AS
SELECT
    json_extract(r.value, '$.schedule_id') AS schedule_id,
    json_extract(r.value, '$.expected_version') AS expected_version,
    json_extract(r.value, '$.expected_state_version') AS expected_state_version,
    json_extract(r.value, '$.next_occurrence_at_utc') AS next_occurrence_at_utc,
    json_extract(r.value, '$.retimed_details') AS retimed_details,
    json_extract(r.value, '$.skipped_details') AS skipped_details
FROM json_each(@p_rows) r;

-- The candidate must still be the draft the plan saw, and the family's current version the one the caller expects.
CREATE TEMP TABLE _pub_target AS
SELECT
    (SELECT c.calendar_id FROM {{schema}}.calendars c
     WHERE c.namespace_id = @p_namespace_id AND c.calendar_name = @p_calendar_name AND c.version = @p_candidate_version
       AND c.calendar_status_code = 10 /* CalendarStatusCode.Draft */) AS candidate_id,
    (SELECT c.calendar_id FROM {{schema}}.calendars c
     WHERE c.namespace_id = @p_namespace_id AND c.calendar_name = @p_calendar_name
       AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */) AS current_id,
    (SELECT c.version FROM {{schema}}.calendars c
     WHERE c.namespace_id = @p_namespace_id AND c.calendar_name = @p_calendar_name
       AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */) AS current_version;

CREATE TEMP TABLE _pub_valid AS
SELECT 1 AS ok
WHERE EXISTS (
        SELECT 1 FROM _pub_target t
        WHERE t.candidate_id IS NOT NULL AND COALESCE(t.current_version, 0) = @p_expected_current_version)
    AND NOT EXISTS (
        SELECT 1
        FROM _pub_rows r
        LEFT JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
        LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = r.schedule_id
        WHERE s.version IS NOT r.expected_version OR st.version IS NOT r.expected_state_version);

UPDATE {{schema}}.calendars
SET calendar_status_code = 30 /* CalendarStatusCode.Superseded */, modified_at_utc = {{now}}
WHERE calendar_id = (SELECT t.current_id FROM _pub_target t) AND EXISTS (SELECT 1 FROM _pub_valid);

UPDATE {{schema}}.calendars
SET calendar_status_code = 20 /* CalendarStatusCode.Published */, published_at_utc = {{now}}, modified_at_utc = {{now}}
WHERE calendar_id = (SELECT t.candidate_id FROM _pub_target t) AND EXISTS (SELECT 1 FROM _pub_valid);

-- The retime events are written before the cursor moves, as registration does: the discarded range first, then
-- the retime, each from the details the plan carries.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    106 /* EventCode.ScheduleSkipped */, {{now}}, s.namespace_id,
    CASE WHEN s.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(s.job_id, s.definition_id),
    s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    jr.execution_number,
    1 /* JobPayloadFormat.Json */, CAST(r.skipped_details AS BLOB)
FROM _pub_rows r
INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
LEFT JOIN {{schema}}.jobs j ON j.job_id = s.job_id
LEFT JOIN {{schema}}.jobs_state jr ON jr.job_id = s.job_id
WHERE
    r.skipped_details IS NOT NULL
    AND EXISTS (SELECT 1 FROM _pub_valid)
    AND COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */
ORDER BY r.schedule_id;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    107 /* EventCode.ScheduleRetimed */, {{now}}, s.namespace_id,
    CASE WHEN s.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(s.job_id, s.definition_id),
    s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    jr.execution_number,
    1 /* JobPayloadFormat.Json */, CAST(r.retimed_details AS BLOB)
FROM _pub_rows r
INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
LEFT JOIN {{schema}}.jobs j ON j.job_id = s.job_id
LEFT JOIN {{schema}}.jobs_state jr ON jr.job_id = s.job_id
WHERE
    EXISTS (SELECT 1 FROM _pub_valid)
    AND COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */
ORDER BY r.schedule_id;

UPDATE {{schema}}.schedules
SET
    modified_at_utc = {{now}},
    version = version + 1
WHERE schedule_id IN (SELECT r.schedule_id FROM _pub_rows r) AND EXISTS (SELECT 1 FROM _pub_valid);

-- The control state stays: a paused schedule wakes at its pause's end, an orphaned one not at all.
UPDATE {{schema}}.schedules_state
SET
    next_occurrence_at_utc = (SELECT r.next_occurrence_at_utc FROM _pub_rows r WHERE r.schedule_id = schedules_state.schedule_id),
    next_evaluation_at_utc = (
        SELECT CASE
            WHEN s.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN s.paused_until_utc
            WHEN s.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */ THEN NULL
            ELSE r.next_occurrence_at_utc END
        FROM _pub_rows r
        INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
        WHERE r.schedule_id = schedules_state.schedule_id),
    evaluation_status_code = (
        SELECT CASE WHEN r.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END
        FROM _pub_rows r WHERE r.schedule_id = schedules_state.schedule_id),
    version = version + 1,
    modified_at_utc = {{now}}
WHERE schedule_id IN (SELECT r.schedule_id FROM _pub_rows r) AND EXISTS (SELECT 1 FROM _pub_valid);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    event_message, details_format_id, details
)
SELECT
    111 /* EventCode.CalendarPublished */, {{now}}, @p_namespace_id,
    15 /* EntityCode.Calendar */, t.candidate_id,
    @p_event_message, @p_details_format_id, @p_details
FROM _pub_target t
WHERE EXISTS (SELECT 1 FROM _pub_valid);

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM _pub_valid) THEN 1 /* ControlAction.Applied */
        WHEN t.candidate_id IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    t.current_version
FROM _pub_target t;
