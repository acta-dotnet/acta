-- One batch, one owned transaction; only the final SELECT returns rows. The schedule lock of every namespace the plan
-- touches, exclusive, in namespace id order, before any row lock: a publication retimes schedules the way a deploy
-- does. The plan was made from a snapshot; every version it read is fenced here, and a moved one rejects it whole.
DROP TABLE IF EXISTS pg_temp._pub_rows;
DROP TABLE IF EXISTS pg_temp._pub_lock;
DROP TABLE IF EXISTS pg_temp._pub_target;
DROP TABLE IF EXISTS pg_temp._pub_states;
DROP TABLE IF EXISTS pg_temp._pub_valid;
CREATE TEMP TABLE _pub_rows ON COMMIT DROP AS
SELECT
    r.schedule_id, r.expected_version, r.expected_state_version, r.next_occurrence_at_utc, r.retimed_details, r.skipped_details
FROM json_to_recordset(@p_rows::json) AS r (
    schedule_id INT, expected_version INT, expected_state_version INT, next_occurrence_at_utc TIMESTAMPTZ, retimed_details TEXT,
    skipped_details TEXT);
CREATE TEMP TABLE _pub_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _pub_states (schedule_id INT NOT NULL, version INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _pub_valid (ok INT NOT NULL) ON COMMIT DROP;

INSERT INTO _pub_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), x.namespace_id)
    FROM (
        SELECT @p_namespace_id AS namespace_id
        UNION
        SELECT s.namespace_id FROM {{schema}}.schedules s WHERE s.schedule_id IN (SELECT r.schedule_id FROM _pub_rows r)
        ORDER BY 1
    ) x
) l;

-- The candidate must still be the draft the plan saw, and the family's current version the one the caller expects.
CREATE TEMP TABLE _pub_target ON COMMIT DROP AS
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

INSERT INTO _pub_states (schedule_id, version)
SELECT st.schedule_id, st.version
FROM {{schema}}.schedules_state st
WHERE st.schedule_id IN (SELECT r.schedule_id FROM _pub_rows r)
ORDER BY st.schedule_id
FOR UPDATE OF st;

INSERT INTO _pub_valid (ok)
SELECT 1
WHERE EXISTS (
        SELECT 1 FROM _pub_target t
        WHERE t.candidate_id IS NOT NULL AND COALESCE(t.current_version, 0) = @p_expected_current_version)
    AND NOT EXISTS (
        SELECT 1
        FROM _pub_rows r
        LEFT JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
        LEFT JOIN _pub_states st ON st.schedule_id = r.schedule_id
        WHERE s.version IS DISTINCT FROM r.expected_version OR st.version IS DISTINCT FROM r.expected_state_version);

UPDATE {{schema}}.calendars c
SET calendar_status_code = 30 /* CalendarStatusCode.Superseded */, modified_at_utc = now()
WHERE c.calendar_id = (SELECT t.current_id FROM _pub_target t) AND EXISTS (SELECT 1 FROM _pub_valid);

UPDATE {{schema}}.calendars c
SET calendar_status_code = 20 /* CalendarStatusCode.Published */, published_at_utc = now(), modified_at_utc = now()
WHERE c.calendar_id = (SELECT t.candidate_id FROM _pub_target t) AND EXISTS (SELECT 1 FROM _pub_valid);

-- The retime events are written before the cursor moves, as registration does, from the rows the plan carries.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    e.event_code, now(), s.namespace_id,
    CASE WHEN s.job_id IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(s.job_id, s.definition_id),
    s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    jr.execution_number,
    1 /* JobPayloadFormat.Json */, convert_to(e.details, 'UTF8')
FROM _pub_rows r
INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
LEFT JOIN {{schema}}.jobs j ON j.job_id = s.job_id
LEFT JOIN {{schema}}.jobs_state jr ON jr.job_id = s.job_id
CROSS JOIN LATERAL (
    SELECT 106 /* EventCode.ScheduleSkipped */ AS event_code, r.skipped_details AS details, 1 AS ord
    UNION ALL
    SELECT 107 /* EventCode.ScheduleRetimed */, r.retimed_details, 2
) e
WHERE
    e.details IS NOT NULL
    AND EXISTS (SELECT 1 FROM _pub_valid)
    AND COALESCE(j.event_recording_code, d.event_recording_code_effective) = 20 /* EventRecordingCode.Audit */
ORDER BY r.schedule_id, e.ord;

UPDATE {{schema}}.schedules s
SET
    modified_at_utc = now(),
    version = s.version + 1
FROM _pub_rows r
WHERE s.schedule_id = r.schedule_id AND EXISTS (SELECT 1 FROM _pub_valid);

-- The control state stays: a paused schedule wakes at its pause's end, an orphaned one not at all.
UPDATE {{schema}}.schedules_state st
SET
    next_occurrence_at_utc = r.next_occurrence_at_utc,
    next_evaluation_at_utc = CASE
        WHEN s.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN s.paused_until_utc
        WHEN s.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */ THEN NULL
        ELSE r.next_occurrence_at_utc END,
    evaluation_status_code = CASE WHEN r.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
    version = st.version + 1,
    modified_at_utc = now()
FROM _pub_rows r
INNER JOIN {{schema}}.schedules s ON s.schedule_id = r.schedule_id
WHERE st.schedule_id = r.schedule_id AND EXISTS (SELECT 1 FROM _pub_valid);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    event_message, details_format_id, details
)
SELECT
    111 /* EventCode.CalendarPublished */, now(), @p_namespace_id,
    15 /* EntityCode.Calendar */, t.candidate_id,
    @p_event_message, @p_details_format_id, @p_details
FROM _pub_target t
WHERE EXISTS (SELECT 1 FROM _pub_valid);

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM _pub_valid) THEN 1 /* ControlAction.Applied */
        WHEN t.candidate_id IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 3 /* ControlAction.Rejected */
    END::SMALLINT AS action,
    t.current_version
FROM _pub_target t;
