INSERT INTO {{schema}}.jobs (
    job_kind_code,
    job_ref,
    lineage_root_job_id,
    parent_job_id,
    correlation_key,
    namespace_id,
    definition_id,
    input_format_id,
    input,
    concurrency_key,
    event_recording_code)
SELECT
    20 /* JobKindCode.PermanentSlot */,
    json_extract(d.value, '$.job_ref'),
    NULL,
    NULL,
    NULL,
    @p_namespace_id,
    json_extract(d.value, '$.definition_id'),
    json_extract(d.value, '$.input_format_id'),
    CASE
        WHEN json_extract(d.value, '$.input_format_id') <> 0 /* JobPayloadFormat.None */
            THEN COALESCE(acta_blob(json_extract(d.value, '$.input')), X'')
    END,
    NULL,
    json_extract(d.value, '$.event_recording_code')
FROM json_each(@p_definitions) d
-- A PerOccurrence definition has no slot: each admitted occurrence is a root of its own.
WHERE json_extract(d.value, '$.scheduled_delivery_code') = 10 /* ScheduledDeliveryCode.Permanent */
ORDER BY json_extract(d.value, '$.definition_id')
ON CONFLICT (definition_id) WHERE job_kind_code = 20 /* JobKindCode.PermanentSlot */
DO UPDATE SET
    input_format_id = excluded.input_format_id,
    input = excluded.input,
    event_recording_code = excluded.event_recording_code;

DROP TABLE IF EXISTS temp._reg_slots;

-- The definition's slot, inserted by this call or found by its kind.
CREATE TEMP TABLE _reg_slots AS
SELECT json_extract(d.value, '$.definition_id') AS definition_id, j.job_id AS slot_id
FROM json_each(@p_definitions) d
JOIN {{schema}}.jobs j
    ON j.definition_id = json_extract(d.value, '$.definition_id')
    AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */;

INSERT INTO {{schema}}.jobs_state (
    job_id,
    namespace_id,
    job_status_code,
    job_priority_code,
    next_run_at_utc,
    execution_number,
    failure_count,
    retention_until_utc,
    modified_at_utc,
    version)
SELECT
    sl.slot_id,
    @p_namespace_id,
    40 /* JobStatusCode.Idle */,
    jd.job_priority_code_effective,
    NULL,
    0,
    0,
    NULL,
    {{now}},
    0
FROM json_each(@p_definitions) d
JOIN temp._reg_slots sl ON sl.definition_id = json_extract(d.value, '$.definition_id')
JOIN {{schema}}.definitions jd
    ON jd.definition_id = json_extract(d.value, '$.definition_id')
ON CONFLICT (job_id) DO UPDATE SET
    job_priority_code = excluded.job_priority_code,
    modified_at_utc = {{now}},
    version = {{schema}}.jobs_state.version + 1
WHERE
    -- The declaration's priority reaches a slot between firings; one an attempt or an operator holds keeps its own.
    {{schema}}.jobs_state.job_status_code = 40 /* JobStatusCode.Idle */
    AND {{schema}}.jobs_state.job_priority_code IS NOT excluded.job_priority_code;

DROP TABLE IF EXISTS temp._reg_schedules;

CREATE TEMP TABLE _reg_schedules AS
SELECT
    sl.slot_id AS job_id,
    json_extract(s.value, '$.definition_id') AS definition_id,
    json_extract(s.value, '$.schedule_name') AS schedule_name,
    json_extract(s.value, '$.expression') AS expression,
    json_extract(s.value, '$.time_zone_id') AS time_zone_id,
    json_extract(s.value, '$.expression_kind_code') AS expression_kind_code,
    json_extract(s.value, '$.misfire_strategy_code') AS misfire_strategy_code,
    json_extract(s.value, '$.next_occurrence_at_utc') AS next_occurrence_at_utc,
    json_extract(s.value, '$.description') AS description,
    json_extract(s.value, '$.interval_anchor_at_utc') AS interval_anchor_at_utc,
    json_extract(s.value, '$.timing_fingerprint') AS timing_fingerprint,
    json_extract(s.value, '$.max_outstanding') AS max_outstanding,
    json_extract(s.value, '$.full_behavior_code') AS full_behavior_code,
    json_extract(s.value, '$.expire_after_seconds') AS expire_after_seconds,
    json_extract(s.value, '$.expire_action_code') AS expire_action_code
FROM json_each(@p_schedules) s
-- A PerOccurrence definition's schedules have no slot, so their job_id is NULL.
LEFT JOIN temp._reg_slots sl ON sl.definition_id = json_extract(s.value, '$.definition_id');

-- A schedule whose timing changed is retimed: the old stream's undecided prefix is discarded, so the watermark moves to
-- now and never back; the plan's cursor is the new timing's first occurrence. Written before the declaration, which
-- overwrites the fingerprint the retime is decided by.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id, execution_number, details_format_id, details)
SELECT
    107 /* EventCode.ScheduleRetimed */, {{now}}, @p_namespace_id, 50 /* EntityCode.Job */, s.job_id,
    s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id), r.execution_number,
    1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' || s.schedule_name || '"}' AS BLOB)
FROM temp._reg_schedules src
JOIN {{schema}}.schedules s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
JOIN {{schema}}.jobs j ON j.job_id = s.job_id
JOIN {{schema}}.jobs_state r ON r.job_id = s.job_id
WHERE s.timing_fingerprint <> src.timing_fingerprint AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

-- A schedule with no slot records its retime against its definition.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, details_format_id, details)
SELECT
    107 /* EventCode.ScheduleRetimed */, {{now}}, @p_namespace_id, 30 /* EntityCode.Definition */, s.definition_id,
    s.definition_id, 1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' || s.schedule_name || '"}' AS BLOB)
FROM temp._reg_schedules src
JOIN {{schema}}.schedules s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
WHERE
    s.job_id IS NULL
    AND s.timing_fingerprint <> src.timing_fingerprint
    AND d.event_recording_code_effective = 20 /* EventRecordingCode.Audit */;

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = src.next_occurrence_at_utc,
    next_evaluation_at_utc = CASE WHEN s.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN s.paused_until_utc ELSE src.next_occurrence_at_utc END,
    evaluation_status_code = CASE WHEN src.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
    version = st.version + 1,
    modified_at_utc = {{now}}
FROM temp._reg_schedules src
JOIN {{schema}}.schedules s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
WHERE
    st.schedule_id = s.schedule_id
    AND (
        st.next_occurrence_at_utc IS NOT src.next_occurrence_at_utc
        OR s.timing_fingerprint <> src.timing_fingerprint
    );

UPDATE {{schema}}.schedules AS tgt
SET
    expression = src.expression,
    time_zone_id = src.time_zone_id,
    expression_kind_code = src.expression_kind_code,
    misfire_strategy_code = src.misfire_strategy_code,
    interval_anchor_at_utc = src.interval_anchor_at_utc,
    timing_fingerprint = src.timing_fingerprint,
    max_outstanding = src.max_outstanding,
    full_behavior_code = src.full_behavior_code,
    expire_after_seconds = src.expire_after_seconds,
    expire_action_code = src.expire_action_code,
    schedule_status_code = CASE WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        THEN 10 /* ScheduleStatusCode.Active */
        ELSE tgt.schedule_status_code END,
    description = src.description,
    modified_at_utc = {{now}},
    version = tgt.version + 1
FROM temp._reg_schedules src
JOIN {{schema}}.schedules cur ON cur.definition_id = src.definition_id AND cur.schedule_name = src.schedule_name
JOIN {{schema}}.schedules_state st ON st.schedule_id = cur.schedule_id
WHERE
    tgt.schedule_id = cur.schedule_id
    AND (
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR tgt.expression IS NOT src.expression
        OR tgt.time_zone_id IS NOT src.time_zone_id
        OR tgt.expression_kind_code IS NOT src.expression_kind_code
        OR tgt.misfire_strategy_code IS NOT src.misfire_strategy_code
        OR tgt.timing_fingerprint IS NOT src.timing_fingerprint
        OR tgt.max_outstanding IS NOT src.max_outstanding
        OR tgt.full_behavior_code IS NOT src.full_behavior_code
        OR tgt.expire_after_seconds IS NOT src.expire_after_seconds
        OR tgt.expire_action_code IS NOT src.expire_action_code
        OR st.next_occurrence_at_utc IS NOT src.next_occurrence_at_utc
        OR tgt.description IS NOT src.description
    );

INSERT INTO {{schema}}.schedules (
    namespace_id,
    job_id,
    definition_id,
    schedule_name,
    schedule_origin_code,
    expression,
    time_zone_id,
    expression_kind_code,
    misfire_strategy_code,
    interval_anchor_at_utc,
    misfire_grace_seconds,
    timing_fingerprint,
    max_outstanding,
    full_behavior_code,
    expire_after_seconds,
    expire_action_code,
    schedule_status_code,
    paused_until_utc,
    description)
SELECT
    @p_namespace_id,
    src.job_id,
    src.definition_id,
    src.schedule_name,
    40 /* ScheduleOriginCode.Definition */,
    src.expression,
    src.time_zone_id,
    src.expression_kind_code,
    src.misfire_strategy_code,
    src.interval_anchor_at_utc,
    60,
    src.timing_fingerprint,
    src.max_outstanding,
    src.full_behavior_code,
    src.expire_after_seconds,
    src.expire_action_code,
    10 /* ScheduleStatusCode.Active */,
    NULL,
    src.description
FROM temp._reg_schedules src
WHERE true
ON CONFLICT (definition_id, schedule_name) DO NOTHING;

INSERT INTO {{schema}}.schedules_state (
    schedule_id,
    namespace_id,
    next_occurrence_at_utc,
    next_evaluation_at_utc,
    evaluation_status_code,
    outstanding_count,
    version)
SELECT
    s.schedule_id,
    @p_namespace_id,
    src.next_occurrence_at_utc,
    src.next_occurrence_at_utc,
    CASE WHEN src.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
    0,
    0
FROM temp._reg_schedules src
JOIN {{schema}}.schedules s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
WHERE true
ON CONFLICT (schedule_id) DO NOTHING;

DROP TABLE IF EXISTS temp._reg_orphaned;

CREATE TEMP TABLE _reg_orphaned AS
SELECT js.schedule_id
FROM json_each(@p_definitions) d
JOIN {{schema}}.schedules js ON js.definition_id = json_extract(d.value, '$.definition_id')
WHERE
    js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
    AND NOT EXISTS (
        SELECT 1
        FROM temp._reg_schedules src
        WHERE
            src.definition_id = js.definition_id
            AND src.schedule_name = js.schedule_name
    );

UPDATE {{schema}}.schedules AS js
SET
    schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
    paused_until_utc = NULL,
    pause_message = NULL,
    modified_at_utc = {{now}},
    version = js.version + 1
FROM temp._reg_orphaned o
WHERE js.schedule_id = o.schedule_id;

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,

    next_evaluation_at_utc = NULL,

    evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

    version = st.version + 1,
    modified_at_utc = {{now}}
FROM temp._reg_orphaned o
WHERE st.schedule_id = o.schedule_id;

-- A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
-- retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace.
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,

    next_evaluation_at_utc = NULL,

    evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

    version = st.version + 1,
    modified_at_utc = {{now}}
FROM {{schema}}.schedules s
JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
WHERE
    st.schedule_id = s.schedule_id
    AND st.namespace_id = @p_namespace_id
    AND st.next_occurrence_at_utc IS NOT NULL
    AND (
        s.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR d.definition_status_code = 240 /* DefinitionStatusCode.Retired */
    );

-- The catalog this call registered; a start that computes the same hash and moves no cursor skips the call.
UPDATE {{schema}}.namespaces
SET catalog_hash = @p_catalog_hash
WHERE
    namespace_id = @p_namespace_id
    AND catalog_hash IS NOT @p_catalog_hash;

SELECT definition_id, slot_id FROM temp._reg_slots;
