-- One owned transaction, SQLite's one writer, so no lock is taken; only the final SELECT returns rows. The statements
-- keep the order the other providers lock in: slots, schedule states, the roots, the envelopes.
DROP TABLE IF EXISTS temp._adm_groups;
DROP TABLE IF EXISTS temp._adm_rows;
DROP TABLE IF EXISTS temp._adm_valid;

CREATE TEMP TABLE _adm_groups AS
SELECT
    json_extract(g.value, '$.ordinal') AS ordinal,
    json_extract(g.value, '$.job_id') AS job_id,
    json_extract(g.value, '$.expected_job_version') AS expected_job_version,
    json_extract(g.value, '$.schedule_id') AS schedule_id,
    json_extract(g.value, '$.expected_state_version') AS expected_state_version,
    json_extract(g.value, '$.admit') AS admit,
    json_extract(g.value, '$.envelope') AS envelope,
    json_extract(g.value, '$.job_ref') AS job_ref,
    json_extract(g.value, '$.deduplication_key') AS deduplication_key,
    json_extract(g.value, '$.trigger_message') AS trigger_message,
    json_extract(g.value, '$.trigger_details') AS trigger_details
FROM json_each(@p_groups) g;

CREATE TEMP TABLE _adm_rows AS
SELECT
    json_extract(a.value, '$.group_ordinal') AS group_ordinal,
    json_extract(a.value, '$.schedule_id') AS schedule_id,
    json_extract(a.value, '$.schedule_name') AS schedule_name,
    json_extract(a.value, '$.expected_version') AS expected_version,
    json_extract(a.value, '$.next_occurrence_at_utc') AS next_occurrence_at_utc,
    json_extract(a.value, '$.next_evaluation_at_utc') AS next_evaluation_at_utc,
    json_extract(a.value, '$.evaluation_status_code') AS evaluation_status_code,
    json_extract(a.value, '$.pause_ended') AS pause_ended,
    json_extract(a.value, '$.admitted_details') AS admitted_details,
    json_extract(a.value, '$.skipped_details') AS skipped_details,
    json_extract(a.value, '$.expired_details') AS expired_details,
    json_extract(a.value, '$.expire_paused') AS expire_paused
FROM json_each(@p_rows) a;

-- A group applies whole or not at all. A slot group needs the slot at the version it read, Idle when it admits, and
-- every member's state version; a per-occurrence group needs its schedule's state version, which every settlement
-- bumps, so the capacity the plan read still holds.
CREATE TEMP TABLE _adm_valid AS
SELECT g.ordinal
FROM temp._adm_groups g
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
LEFT JOIN {{schema}}.schedules_state gs ON gs.schedule_id = g.schedule_id
WHERE
    CASE
        WHEN g.job_id IS NOT NULL
            THEN r.version = g.expected_job_version AND (g.admit = 0 OR r.job_status_code = 40 /* JobStatusCode.Idle */)
        ELSE gs.version = g.expected_state_version
    END
    AND NOT EXISTS (
        SELECT 1
        FROM temp._adm_rows a
        JOIN {{schema}}.schedules_state st ON st.schedule_id = a.schedule_id
        WHERE a.group_ordinal = g.ordinal AND st.version <> a.expected_version
    );

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    failure_count = 0,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (
    SELECT g.job_id FROM temp._adm_groups g JOIN temp._adm_valid v ON v.ordinal = g.ordinal WHERE g.admit = 1 AND g.job_id IS NOT NULL
);

-- A per-occurrence firing is a fresh root: the definition's stored default input, priority, and audit level, the
-- schedule as its provenance, no lane and no tenant, keyed so the same occurrence is never admitted twice. Ready at once.
INSERT INTO {{schema}}.jobs (
    job_kind_code, job_ref, deduplication_key, namespace_id, definition_id, schedule_id,
    input_format_id, input, event_recording_code)
SELECT
    10 /* JobKindCode.Ordinary */, g.job_ref, g.deduplication_key, @p_namespace_id, js.definition_id, g.schedule_id,
    jd.default_input_format_id, jd.default_input, jd.event_recording_code_effective
FROM temp._adm_groups g
JOIN temp._adm_valid v ON v.ordinal = g.ordinal
JOIN {{schema}}.schedules js ON js.schedule_id = g.schedule_id
JOIN {{schema}}.definitions jd ON jd.definition_id = js.definition_id
WHERE g.admit = 1 AND g.job_id IS NULL;

INSERT INTO {{schema}}.jobs_state (
    job_id, namespace_id, lane_id, job_status_code, job_priority_code, next_run_at_utc,
    execution_number, failure_count, retention_until_utc, modified_at_utc, version)
SELECT
    j.job_id, j.namespace_id, NULL, 10 /* JobStatusCode.Ready */, jd.job_priority_code_effective, {{now}},
    0, 0, NULL, {{now}}, 0
FROM temp._adm_groups g
JOIN temp._adm_valid v ON v.ordinal = g.ordinal
JOIN {{schema}}.jobs j ON j.job_ref = g.job_ref
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE g.admit = 1 AND g.job_id IS NULL;

-- The envelope: on a slot it replaces the settled one of the previous firing, since a Pending one never exists on an
-- Idle slot; on a root it is the first.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    COALESCE(g.job_id, root.job_id), 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, CAST(g.envelope AS BLOB), {{now}}, {{now}}, 0
FROM temp._adm_groups g
JOIN temp._adm_valid v ON v.ordinal = g.ordinal
LEFT JOIN {{schema}}.jobs root ON root.job_ref = g.job_ref
WHERE g.admit = 1
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = excluded.checkpoint_status_code,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = version + 1;

-- The cursors move, and an admitted root is charged to its schedule.
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = a.next_occurrence_at_utc,
    next_evaluation_at_utc = a.next_evaluation_at_utc,
    evaluation_status_code = a.evaluation_status_code,
    outstanding_count = st.outstanding_count + CASE WHEN g.admit = 1 AND g.job_id IS NULL THEN 1 ELSE 0 END,
    modified_at_utc = {{now}},
    version = st.version + 1
FROM temp._adm_rows a
JOIN temp._adm_valid v ON v.ordinal = a.group_ordinal
JOIN temp._adm_groups g ON g.ordinal = a.group_ordinal
WHERE st.schedule_id = a.schedule_id;

-- A manual root moves no cursor; it is charged on its own.
UPDATE {{schema}}.schedules_state
SET
    outstanding_count = outstanding_count + 1,
    modified_at_utc = {{now}},
    version = version + 1
WHERE schedule_id IN (
    SELECT g.schedule_id
    FROM temp._adm_groups g
    JOIN temp._adm_valid v ON v.ordinal = g.ordinal
    WHERE g.admit = 1 AND g.job_id IS NULL AND NOT EXISTS (SELECT 1 FROM temp._adm_rows a WHERE a.group_ordinal = g.ordinal)
);

-- An elapsed timed pause ends here, the one place the admitter touches the schedules row.
UPDATE {{schema}}.schedules AS js
SET
    schedule_status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = {{now}},
    version = js.version + 1
FROM temp._adm_rows a
JOIN temp._adm_valid v ON v.ordinal = a.group_ordinal
WHERE js.schedule_id = a.schedule_id AND a.pause_ended = 1 AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */;

-- An occurrence that expired unadmitted under the Pause action stops the schedule for an operator.
UPDATE {{schema}}.schedules AS js
SET
    schedule_status_code = 30 /* ScheduleStatusCode.Paused */,
    paused_until_utc = NULL,
    pause_message = 'Paused by the expiration policy: an occurrence expired unadmitted.',
    modified_at_utc = {{now}},
    version = js.version + 1
FROM temp._adm_rows a
JOIN temp._adm_valid v ON v.ordinal = a.group_ordinal
WHERE js.schedule_id = a.schedule_id AND a.expire_paused = 1 AND js.schedule_status_code = 10 /* ScheduleStatusCode.Active */;

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
    k.event_code, {{now}}, @p_namespace_id,
    CASE WHEN k.on_job IS NULL THEN 30 /* EntityCode.Definition */ ELSE 50 /* EntityCode.Job */ END,
    COALESCE(k.on_job, k.definition_id),
    k.definition_id, k.tenant_id, k.lineage_root_job_id,
    k.execution_number,
    k.event_message, 1 /* JobPayloadFormat.Json */, CAST(k.details AS BLOB)
FROM (
    SELECT
        x.schedule_id, x.ord, x.event_code, x.details, x.event_message,
        CASE WHEN g.job_id IS NOT NULL THEN g.job_id WHEN x.ord IN (3, 6) THEN root.job_id END AS on_job,
        js.definition_id, j.tenant_id,
        COALESCE(j.lineage_root_job_id, j.job_id, root.job_id) AS lineage_root_job_id,
        COALESCE(r.execution_number, 0) AS execution_number,
        COALESCE(j.event_recording_code, jd.event_recording_code_effective) AS event_recording_code
    FROM (
        SELECT a.group_ordinal, a.schedule_id, 1 AS ord, 106 /* EventCode.ScheduleSkipped */ AS event_code, a.expired_details AS details, NULL AS event_message
        FROM temp._adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 2, 106 /* EventCode.ScheduleSkipped */, a.skipped_details, NULL
        FROM temp._adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 3, 105 /* EventCode.ScheduleAdmitted */, a.admitted_details, NULL
        FROM temp._adm_rows a
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 4, 102 /* EventCode.SchedulePauseExpired */, '{"schedule":"' || a.schedule_name || '"}', NULL
        FROM temp._adm_rows a
        WHERE a.pause_ended = 1
        UNION ALL
        SELECT a.group_ordinal, a.schedule_id, 5, 100 /* EventCode.SchedulePaused */, '{"schedule":"' || a.schedule_name || '","reason":"expired"}', NULL
        FROM temp._adm_rows a
        WHERE a.expire_paused = 1
        UNION ALL
        SELECT g.ordinal, g.schedule_id, 6, 104 /* EventCode.ScheduleTriggered */, g.trigger_details, g.trigger_message
        FROM temp._adm_groups g
        WHERE g.job_id IS NULL AND g.admit = 1 AND g.trigger_details IS NOT NULL
    ) x
    JOIN temp._adm_valid v ON v.ordinal = x.group_ordinal
    JOIN temp._adm_groups g ON g.ordinal = x.group_ordinal
    JOIN {{schema}}.schedules js ON js.schedule_id = x.schedule_id
    JOIN {{schema}}.definitions jd ON jd.definition_id = js.definition_id
    LEFT JOIN {{schema}}.jobs j ON j.job_id = g.job_id
    LEFT JOIN {{schema}}.jobs_state r ON r.job_id = g.job_id
    LEFT JOIN {{schema}}.jobs root ON root.job_ref = g.job_ref
) k
WHERE k.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.details IS NOT NULL
ORDER BY k.schedule_id, k.ord;

SELECT g.ordinal, CASE WHEN v.ordinal IS NULL THEN 0 ELSE 1 END AS applied
FROM temp._adm_groups g
LEFT JOIN temp._adm_valid v ON v.ordinal = g.ordinal;
