-- One owned transaction, SQLite's one writer, so no lock is taken; only the final SELECT returns rows. The statements
-- keep the order the other providers lock in: slots, members' state rows, the envelope.
DROP TABLE IF EXISTS temp._adm_groups;
DROP TABLE IF EXISTS temp._adm_rows;
DROP TABLE IF EXISTS temp._adm_valid;

CREATE TEMP TABLE _adm_groups AS
SELECT
    json_extract(g.value, '$.job_id') AS job_id,
    json_extract(g.value, '$.expected_job_version') AS expected_job_version,
    json_extract(g.value, '$.admit') AS admit,
    json_extract(g.value, '$.envelope') AS envelope
FROM json_each(@p_groups) g;

CREATE TEMP TABLE _adm_rows AS
SELECT
    json_extract(a.value, '$.schedule_id') AS schedule_id,
    json_extract(a.value, '$.job_id') AS job_id,
    json_extract(a.value, '$.schedule_name') AS schedule_name,
    json_extract(a.value, '$.expected_version') AS expected_version,
    json_extract(a.value, '$.next_occurrence_at_utc') AS next_occurrence_at_utc,
    json_extract(a.value, '$.decided_through_at_utc') AS decided_through_at_utc,
    json_extract(a.value, '$.next_evaluation_at_utc') AS next_evaluation_at_utc,
    json_extract(a.value, '$.evaluation_status_code') AS evaluation_status_code,
    json_extract(a.value, '$.pause_ended') AS pause_ended,
    json_extract(a.value, '$.admitted_details') AS admitted_details,
    json_extract(a.value, '$.skipped_details') AS skipped_details
FROM json_each(@p_rows) a;

-- A group applies whole or not at all: an admitting group needs the slot Idle at the version it read, a cursor-only
-- group needs only the version, and every member's state version must hold.
CREATE TEMP TABLE _adm_valid AS
SELECT g.job_id
FROM temp._adm_groups g
JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
WHERE
    r.version = g.expected_job_version
    AND (g.admit = 0 OR r.job_status_code = 40 /* JobStatusCode.Idle */)
    AND NOT EXISTS (
        SELECT 1
        FROM temp._adm_rows a
        JOIN {{schema}}.schedules_state st ON st.schedule_id = a.schedule_id
        WHERE a.job_id = g.job_id AND st.version <> a.expected_version
    );

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    failure_count = 0,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT g.job_id FROM temp._adm_groups g JOIN temp._adm_valid v ON v.job_id = g.job_id WHERE g.admit = 1);

-- The envelope replaces the settled one of the previous firing; a Pending one never exists on an Idle slot.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    g.job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, CAST(g.envelope AS BLOB), {{now}}, {{now}}, 0
FROM temp._adm_groups g
JOIN temp._adm_valid v ON v.job_id = g.job_id
WHERE g.admit = 1
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = excluded.checkpoint_status_code,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = version + 1;

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = a.next_occurrence_at_utc,
    decided_through_at_utc = a.decided_through_at_utc,
    next_evaluation_at_utc = a.next_evaluation_at_utc,
    evaluation_status_code = a.evaluation_status_code,
    modified_at_utc = {{now}},
    version = st.version + 1
FROM temp._adm_rows a
JOIN temp._adm_valid v ON v.job_id = a.job_id
WHERE st.schedule_id = a.schedule_id;

-- An elapsed timed pause ends here, the one place the admitter touches the schedules row.
UPDATE {{schema}}.schedules AS js
SET
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = {{now}},
    version = js.version + 1
FROM temp._adm_rows a
JOIN temp._adm_valid v ON v.job_id = a.job_id
WHERE js.schedule_id = a.schedule_id AND a.pause_ended = 1 AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

-- Decision evidence at Audit: an admission, a skipped range, an elapsed pause. A schedule name is dotted kebab ASCII.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    k.event_code, {{now}}, j.namespace_id,
    50 /* EntityCode.Job */, j.job_id,
    j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    1 /* JobPayloadFormat.Json */, CAST(k.details AS BLOB)
FROM (
    SELECT a.schedule_id, a.job_id, 1 AS ord, 105 /* EventCode.ScheduleAdmitted */ AS event_code, a.admitted_details AS details
    FROM temp._adm_rows a
    UNION ALL
    SELECT a.schedule_id, a.job_id, 2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details
    FROM temp._adm_rows a
    UNION ALL
    SELECT a.schedule_id, a.job_id, 3, 102 /* EventCode.SchedulePauseExpired */, '{"schedule":"' || a.schedule_name || '"}'
    FROM temp._adm_rows a
    WHERE a.pause_ended = 1
) k
JOIN temp._adm_valid v ON v.job_id = k.job_id
JOIN {{schema}}.jobs j ON j.job_id = k.job_id
JOIN {{schema}}.jobs_state r ON r.job_id = k.job_id
WHERE j.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
ORDER BY k.schedule_id, k.ord;

SELECT g.job_id, CASE WHEN v.job_id IS NULL THEN 0 ELSE 1 END AS applied
FROM temp._adm_groups g
LEFT JOIN temp._adm_valid v ON v.job_id = g.job_id;
