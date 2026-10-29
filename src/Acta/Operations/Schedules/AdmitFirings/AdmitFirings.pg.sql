-- One batch, one owned transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes
-- before any row lock (docs/sql-internals.md, "Schedule lock"), then the slots' jobs_state rows in id order, then the
-- members' state rows in id order, then the envelope: the order a settlement takes them in.
DROP TABLE IF EXISTS pg_temp._adm_groups;
DROP TABLE IF EXISTS pg_temp._adm_rows;
DROP TABLE IF EXISTS pg_temp._adm_slots;
DROP TABLE IF EXISTS pg_temp._adm_states;
DROP TABLE IF EXISTS pg_temp._adm_valid;
DROP TABLE IF EXISTS pg_temp._adm_lock;
CREATE TEMP TABLE _adm_groups ON COMMIT DROP AS
SELECT g.job_id, g.expected_job_version, g.admit, g.envelope
FROM json_to_recordset(@p_groups::json) AS g (job_id BIGINT, expected_job_version INT, admit BOOLEAN, envelope TEXT);
CREATE TEMP TABLE _adm_rows ON COMMIT DROP AS
SELECT
    a.schedule_id, a.job_id, a.schedule_name, a.expected_version,
    a.next_occurrence_at_utc, a.decided_through_at_utc, a.next_evaluation_at_utc, a.evaluation_status_code,
    a.pause_ended, a.admitted_details, a.skipped_details
FROM json_to_recordset(@p_rows::json) AS a (
    schedule_id INT, job_id BIGINT, schedule_name VARCHAR, expected_version INT,
    next_occurrence_at_utc TIMESTAMPTZ, decided_through_at_utc TIMESTAMPTZ, next_evaluation_at_utc TIMESTAMPTZ,
    evaluation_status_code SMALLINT, pause_ended BOOLEAN, admitted_details TEXT, skipped_details TEXT);
CREATE TEMP TABLE _adm_slots (job_id BIGINT NOT NULL, valid BOOLEAN NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_states (schedule_id INT NOT NULL, valid BOOLEAN NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_valid (job_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_lock (granted INT NOT NULL) ON COMMIT DROP;

INSERT INTO _adm_lock (granted)
SELECT 1 FROM (SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), @p_namespace_id)) AS l;

-- An admitting group needs the slot Idle at the version it read; a cursor-only group needs only the version.
INSERT INTO _adm_slots (job_id, valid)
SELECT g.job_id, r.version = g.expected_job_version AND (NOT g.admit OR r.job_status_code = 40 /* JobStatusCode.Idle */)
FROM _adm_groups g
INNER JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
ORDER BY r.job_id
FOR UPDATE OF r;

INSERT INTO _adm_states (schedule_id, valid)
SELECT a.schedule_id, st.version = a.expected_version
FROM _adm_rows a
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = a.schedule_id
ORDER BY st.schedule_id
FOR UPDATE OF st;

-- A group applies whole or not at all: a slot or a member another writer moved since the plan read it replans.
INSERT INTO _adm_valid (job_id)
SELECT s.job_id
FROM _adm_slots s
WHERE
    s.valid
    AND NOT EXISTS (
        SELECT 1
        FROM _adm_rows a
        INNER JOIN _adm_states x ON x.schedule_id = a.schedule_id
        WHERE a.job_id = s.job_id AND NOT x.valid
    );

UPDATE {{schema}}.jobs_state r
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = now(),
    failure_count = 0,
    modified_at_utc = now(),
    version = r.version + 1
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.job_id = g.job_id
WHERE r.job_id = g.job_id AND g.admit;

-- The envelope replaces the settled one of the previous firing; a Pending one never exists on an Idle slot.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    g.job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, convert_to(g.envelope, 'UTF8'), now(), now(), 0
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.job_id = g.job_id
WHERE g.admit
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = EXCLUDED.checkpoint_status_code,
    value_format_id = EXCLUDED.value_format_id,
    value = EXCLUDED.value,
    modified_at_utc = now(),
    version = {{schema}}.checkpoints.version + 1;

UPDATE {{schema}}.schedules_state st
SET
    next_occurrence_at_utc = a.next_occurrence_at_utc,
    decided_through_at_utc = a.decided_through_at_utc,
    next_evaluation_at_utc = a.next_evaluation_at_utc,
    evaluation_status_code = a.evaluation_status_code,
    modified_at_utc = now(),
    version = st.version + 1
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.job_id = a.job_id
WHERE st.schedule_id = a.schedule_id;

-- An elapsed timed pause ends here, the one place the admitter touches the schedules row.
UPDATE {{schema}}.schedules js
SET
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = now(),
    version = js.version + 1
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.job_id = a.job_id
WHERE js.schedule_id = a.schedule_id AND a.pause_ended AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

-- Decision evidence at Audit: an admission, a skipped range, an elapsed pause. A schedule name is dotted kebab ASCII.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    k.event_code, now(), j.namespace_id,
    50 /* EntityCode.Job */, j.job_id,
    j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    1 /* JobPayloadFormat.Json */, convert_to(k.details, 'UTF8')
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.job_id = a.job_id
INNER JOIN {{schema}}.jobs j ON j.job_id = a.job_id
INNER JOIN {{schema}}.jobs_state r ON r.job_id = a.job_id
CROSS JOIN LATERAL (
    VALUES
    (1, 105 /* EventCode.ScheduleAdmitted */, a.admitted_details),
    (2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details),
    (3, 102 /* EventCode.SchedulePauseExpired */, CASE WHEN a.pause_ended THEN '{"schedule":"' || a.schedule_name || '"}' END)
) AS k (ord, event_code, details)
WHERE j.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
ORDER BY a.schedule_id, k.ord;

SELECT s.job_id, (v.job_id IS NOT NULL) AS applied
FROM _adm_slots s
LEFT JOIN _adm_valid v ON v.job_id = s.job_id;
