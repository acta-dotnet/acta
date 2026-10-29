-- One batch, one owned transaction; only the final SELECT returns rows. The namespace's schedule lock, shared, comes
-- before any row lock (docs/sql-internals.md, "Schedule lock"), then the slots' jobs_state rows in id order, then the
-- schedules' state rows in id order, then the roots and the envelopes: the order a settlement takes them in.
DROP TABLE IF EXISTS pg_temp._adm_groups;
DROP TABLE IF EXISTS pg_temp._adm_rows;
DROP TABLE IF EXISTS pg_temp._adm_slots;
DROP TABLE IF EXISTS pg_temp._adm_states;
DROP TABLE IF EXISTS pg_temp._adm_valid;
DROP TABLE IF EXISTS pg_temp._adm_lock;
CREATE TEMP TABLE _adm_groups ON COMMIT DROP AS
SELECT
    g.ordinal, g.job_id, g.expected_job_version, g.schedule_id, g.expected_state_version, g.admit, g.envelope,
    g.job_ref, g.deduplication_key, g.trigger_message, g.trigger_details
FROM json_to_recordset(@p_groups::json) AS g (
    ordinal INT, job_id BIGINT, expected_job_version INT, schedule_id INT, expected_state_version INT, admit BOOLEAN,
    envelope TEXT, job_ref UUID, deduplication_key VARCHAR, trigger_message VARCHAR, trigger_details TEXT);
CREATE TEMP TABLE _adm_rows ON COMMIT DROP AS
SELECT
    a.group_ordinal, a.schedule_id, a.schedule_name, a.expected_version,
    a.next_occurrence_at_utc, a.next_evaluation_at_utc, a.evaluation_status_code,
    a.pause_ended, a.admitted_details, a.skipped_details, a.expired_details, a.expire_paused
FROM json_to_recordset(@p_rows::json) AS a (
    group_ordinal INT, schedule_id INT, schedule_name VARCHAR, expected_version INT,
    next_occurrence_at_utc TIMESTAMPTZ, next_evaluation_at_utc TIMESTAMPTZ,
    evaluation_status_code SMALLINT, pause_ended BOOLEAN, admitted_details TEXT, skipped_details TEXT,
    expired_details TEXT, expire_paused BOOLEAN);
CREATE TEMP TABLE _adm_slots (job_id BIGINT NOT NULL, valid BOOLEAN NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_states (schedule_id INT NOT NULL, version INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_valid (ordinal INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _adm_lock (granted INT NOT NULL) ON COMMIT DROP;

INSERT INTO _adm_lock (granted)
SELECT 1 FROM (SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), @p_namespace_id)) AS l;

-- An admitting slot group needs the slot Idle at the version it read; a cursor-only group needs only the version.
INSERT INTO _adm_slots (job_id, valid)
SELECT g.job_id, r.version = g.expected_job_version AND (NOT g.admit OR r.job_status_code = 40 /* JobStatusCode.Idle */)
FROM _adm_groups g
INNER JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
WHERE g.job_id IS NOT NULL
ORDER BY r.job_id
FOR UPDATE OF r;

-- Every schedule state the plan touches, a member's or a per-occurrence group's own, in id order.
INSERT INTO _adm_states (schedule_id, version)
SELECT st.schedule_id, st.version
FROM {{schema}}.schedules_state st
WHERE st.schedule_id IN (
    SELECT a.schedule_id FROM _adm_rows a
    UNION
    SELECT g.schedule_id FROM _adm_groups g WHERE g.schedule_id IS NOT NULL
)
ORDER BY st.schedule_id
FOR UPDATE OF st;

-- A group applies whole or not at all: a slot, a member, or a per-occurrence schedule's own state another writer
-- moved since the plan read it replans; the state version is what every settlement bumps, so the capacity the plan
-- read still holds.
INSERT INTO _adm_valid (ordinal)
SELECT g.ordinal
FROM _adm_groups g
LEFT JOIN _adm_slots s ON s.job_id = g.job_id
LEFT JOIN _adm_states gs ON gs.schedule_id = g.schedule_id
WHERE
    CASE WHEN g.job_id IS NOT NULL THEN s.valid ELSE gs.version = g.expected_state_version END
    AND NOT EXISTS (
        SELECT 1
        FROM _adm_rows a
        INNER JOIN _adm_states x ON x.schedule_id = a.schedule_id
        WHERE a.group_ordinal = g.ordinal AND x.version <> a.expected_version
    );

UPDATE {{schema}}.jobs_state r
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = now(),
    failure_count = 0,
    modified_at_utc = now(),
    version = r.version + 1
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.ordinal = g.ordinal
WHERE r.job_id = g.job_id AND g.admit;

-- A per-occurrence firing is a fresh root: the definition's stored default input, priority, and audit level, the
-- schedule as its provenance, no lane and no tenant, keyed so the same occurrence is never admitted twice. Ready at once.
INSERT INTO {{schema}}.jobs (
    job_kind_code, job_ref, deduplication_key, namespace_id, definition_id, schedule_id,
    input_format_id, input, event_recording_code, created_at_utc)
SELECT
    10 /* JobKindCode.Ordinary */, g.job_ref, g.deduplication_key, @p_namespace_id, js.definition_id, g.schedule_id,
    jd.default_input_format_id, jd.default_input, jd.event_recording_code_effective, now()
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.ordinal = g.ordinal
INNER JOIN {{schema}}.schedules js ON js.schedule_id = g.schedule_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = js.definition_id
WHERE g.admit AND g.job_id IS NULL
ORDER BY g.ordinal;

INSERT INTO {{schema}}.jobs_state (
    job_id, namespace_id, lane_id, job_status_code, job_priority_code, next_run_at_utc,
    execution_number, failure_count, retention_until_utc, modified_at_utc, version)
SELECT
    j.job_id, j.namespace_id, NULL, 10 /* JobStatusCode.Ready */, jd.job_priority_code_effective, now(),
    0, 0, NULL, now(), 0
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.ordinal = g.ordinal
INNER JOIN {{schema}}.jobs j ON j.job_ref = g.job_ref
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE g.admit AND g.job_id IS NULL;

-- The envelope: on a slot it replaces the settled one of the previous firing, since a Pending one never exists on an
-- Idle slot; on a root it is the first.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    COALESCE(g.job_id, root.job_id), 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, convert_to(g.envelope, 'UTF8'), now(), now(), 0
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.ordinal = g.ordinal
LEFT JOIN {{schema}}.jobs root ON root.job_ref = g.job_ref
WHERE g.admit
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = EXCLUDED.checkpoint_status_code,
    value_format_id = EXCLUDED.value_format_id,
    value = EXCLUDED.value,
    modified_at_utc = now(),
    version = {{schema}}.checkpoints.version + 1;

-- The cursors move, and an admitted root is charged to its schedule.
UPDATE {{schema}}.schedules_state st
SET
    next_occurrence_at_utc = a.next_occurrence_at_utc,
    next_evaluation_at_utc = a.next_evaluation_at_utc,
    evaluation_status_code = a.evaluation_status_code,
    outstanding_count = st.outstanding_count + CASE WHEN g.admit AND g.job_id IS NULL THEN 1 ELSE 0 END,
    modified_at_utc = now(),
    version = st.version + 1
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.ordinal = a.group_ordinal
INNER JOIN _adm_groups g ON g.ordinal = a.group_ordinal
WHERE st.schedule_id = a.schedule_id;

-- A manual root moves no cursor; it is charged on its own.
UPDATE {{schema}}.schedules_state st
SET
    outstanding_count = st.outstanding_count + 1,
    modified_at_utc = now(),
    version = st.version + 1
FROM _adm_groups g
INNER JOIN _adm_valid v ON v.ordinal = g.ordinal
WHERE
    st.schedule_id = g.schedule_id
    AND g.admit
    AND g.job_id IS NULL
    AND NOT EXISTS (SELECT 1 FROM _adm_rows a WHERE a.group_ordinal = g.ordinal);

-- An elapsed timed pause ends here, the one place the admitter touches the schedules row.
UPDATE {{schema}}.schedules js
SET
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = now(),
    version = js.version + 1
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.ordinal = a.group_ordinal
WHERE js.schedule_id = a.schedule_id AND a.pause_ended AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

-- An occurrence that expired unadmitted under the Pause action stops the schedule for an operator.
UPDATE {{schema}}.schedules js
SET
    schedule_status_code = 30 /* ScheduleStatusCode.Paused */,
    paused_until_utc = NULL,
    pause_message = 'Paused by the expiration policy: an occurrence expired unadmitted.',
    modified_at_utc = now(),
    version = js.version + 1
FROM _adm_rows a
INNER JOIN _adm_valid v ON v.ordinal = a.group_ordinal
WHERE js.schedule_id = a.schedule_id AND a.expire_paused AND js.schedule_status_code = 10 /* ScheduleStatusCode.Active */;

-- Decision evidence at Audit: an expired range, a skipped range, an admission, an elapsed pause, an expiry pause, a
-- manual trigger. A slot's decisions hang on the slot; a per-occurrence admission or trigger on its root, the rest of a
-- per-occurrence schedule's on the definition. A schedule name is dotted kebab ASCII.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    k.event_code, now(), @p_namespace_id,
    CASE WHEN k.on_job IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(k.on_job, k.definition_id),
    k.definition_id, k.tenant_id, k.lineage_root_job_id,
    k.execution_number,
    k.event_message, 1 /* JobPayloadFormat.Json */, convert_to(k.details, 'UTF8')
FROM (
    SELECT
        x.schedule_id, x.ord, x.event_code, x.details, x.event_message,
        CASE WHEN g.job_id IS NOT NULL THEN g.job_id WHEN x.ord IN (3, 6) THEN root.job_id END AS on_job,
        js.definition_id, j.tenant_id,
        COALESCE(j.lineage_root_job_id, j.job_id, root.job_id) AS lineage_root_job_id,
        COALESCE(r.execution_number, 0) AS execution_number,
        COALESCE(j.event_recording_code, jd.event_recording_code_effective) AS event_recording_code
    FROM (
        SELECT a.group_ordinal, a.schedule_id, 1 AS ord, 106 /* EventCode.ScheduleSkipped */ AS event_code, a.expired_details AS details, NULL::VARCHAR AS event_message
        FROM _adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details, NULL
        FROM _adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 3, 105 /* EventCode.ScheduleAdmitted */, a.admitted_details, NULL
        FROM _adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 4, 102 /* EventCode.SchedulePauseExpired */, '{"schedule":"' || a.schedule_name || '"}', NULL
        FROM _adm_rows a
        WHERE a.pause_ended
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 5, 100 /* EventCode.SchedulePaused */, '{"schedule":"' || a.schedule_name || '","reason":"expired"}', NULL
        FROM _adm_rows a
        WHERE a.expire_paused
        UNION ALL
        SELECT g.ordinal, g.schedule_id, 6, 104 /* EventCode.ScheduleTriggered */, g.trigger_details, g.trigger_message
        FROM _adm_groups g
        WHERE g.job_id IS NULL AND g.admit AND g.trigger_details IS NOT NULL
    ) x
    INNER JOIN _adm_valid v ON v.ordinal = x.group_ordinal
    INNER JOIN _adm_groups g ON g.ordinal = x.group_ordinal
    INNER JOIN {{schema}}.schedules js ON js.schedule_id = x.schedule_id
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = js.definition_id
    LEFT JOIN {{schema}}.jobs j ON j.job_id = g.job_id
    LEFT JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
    LEFT JOIN {{schema}}.jobs root ON root.job_ref = g.job_ref
) k
WHERE k.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
ORDER BY k.schedule_id, k.ord;

SELECT g.ordinal, (v.ordinal IS NOT NULL) AS applied
FROM _adm_groups g
LEFT JOIN _adm_valid v ON v.ordinal = g.ordinal;
