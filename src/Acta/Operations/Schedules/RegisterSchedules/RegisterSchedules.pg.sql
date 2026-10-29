-- One batch, one implicit transaction; only the final SELECT returns rows.
-- The namespace's schedule lock, exclusive, before any row lock (docs/sql-internals.md, "Schedule lock").
DROP TABLE IF EXISTS pg_temp._reg_lock;
CREATE TEMP TABLE _reg_lock ON COMMIT DROP AS
SELECT 1 AS granted FROM (SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), @p_namespace_id)) AS l;

DROP TABLE IF EXISTS pg_temp._reg_slots;
CREATE TEMP TABLE _reg_slots (definition_id INT PRIMARY KEY, slot_id BIGINT NOT NULL) ON COMMIT DROP;

WITH defs AS (
    SELECT *
    FROM unnest(
        @p_d_job_ref, @p_d_definition_id, @p_d_input_format_id, @p_d_input,
        @p_d_event_recording_code, @p_d_scheduled_delivery_code)
    AS d(job_ref, definition_id, input_format_id, input, event_recording_code, scheduled_delivery_code)
),
upserted AS (
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
        event_recording_code,
        created_at_utc)
    SELECT
        20 /* JobKindCode.PermanentSlot */,
        d.job_ref,
        NULL,
        NULL,
        NULL,
        @p_namespace_id,
        d.definition_id,
        d.input_format_id,
        d.input,
        NULL,
        d.event_recording_code,
        now()
    FROM defs AS d
    -- A PerOccurrence definition has no slot: each admitted occurrence is a root of its own.
    WHERE d.scheduled_delivery_code = 10 /* ScheduledDeliveryCode.Permanent */
    ORDER BY d.definition_id
    ON CONFLICT (definition_id) WHERE job_kind_code = 20 /* JobKindCode.PermanentSlot */
    DO UPDATE SET
        input_format_id = EXCLUDED.input_format_id,
        input = EXCLUDED.input,
        event_recording_code = EXCLUDED.event_recording_code
    RETURNING {{schema}}.jobs.definition_id, {{schema}}.jobs.job_id
)
INSERT INTO _reg_slots (definition_id, slot_id)
SELECT definition_id, job_id FROM upserted;

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
    now(),
    0
FROM unnest(@p_d_definition_id) AS d(definition_id)
INNER JOIN _reg_slots AS sl ON sl.definition_id = d.definition_id
INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
-- The slot rows lock in key order: a completion that settles a child locks its slot parent's row
-- without the schedule lock.
ORDER BY sl.slot_id
ON CONFLICT (job_id) DO UPDATE SET
    job_priority_code = EXCLUDED.job_priority_code,
    modified_at_utc = now(),
    version = {{schema}}.jobs_state.version + 1
WHERE
    -- The declaration's priority reaches a slot between firings; one an attempt or an operator holds keeps its own.
    {{schema}}.jobs_state.job_status_code = 40 /* JobStatusCode.Idle */
    AND {{schema}}.jobs_state.job_priority_code IS DISTINCT FROM EXCLUDED.job_priority_code;

DROP TABLE IF EXISTS pg_temp._reg_schedules;
CREATE TEMP TABLE _reg_schedules ON COMMIT DROP AS
SELECT
    sl.slot_id AS job_id,
    s.definition_id,
    s.schedule_name,
    s.expression,
    s.time_zone_id,
    s.expression_kind_code,
    s.misfire_strategy_code,
    s.next_occurrence_at_utc,
    s.description,
    s.interval_anchor_at_utc,
    s.timing_fingerprint,
    s.max_outstanding,
    s.full_behavior_code,
    s.expire_after_seconds,
    s.expire_action_code,
    s.calendar_origin_code,
    s.calendar_version,
    s.availability_calendar_name,
    s.availability_calendar_origin_code,
    s.availability_calendar_version
FROM unnest(
    @p_s_definition_id, @p_s_schedule_name, @p_s_expression, @p_s_time_zone,
    @p_s_expression_kind, @p_s_misfire, @p_s_next_occurrence_at_utc, @p_s_description,
    @p_s_interval_anchor_at_utc, @p_s_timing_fingerprint, @p_s_max_outstanding, @p_s_full_behavior_code,
    @p_s_expire_after_seconds, @p_s_expire_action_code, @p_s_calendar_origin_code, @p_s_calendar_version,
    @p_s_availability_calendar_name, @p_s_availability_calendar_origin_code, @p_s_availability_calendar_version)
AS s(
    definition_id, schedule_name, expression, time_zone_id,
    expression_kind_code, misfire_strategy_code, next_occurrence_at_utc, description,
    interval_anchor_at_utc, timing_fingerprint, max_outstanding, full_behavior_code,
    expire_after_seconds, expire_action_code, calendar_origin_code, calendar_version,
    availability_calendar_name, availability_calendar_origin_code, availability_calendar_version)
-- A PerOccurrence definition's schedules have no slot, so their job_id is NULL.
LEFT JOIN _reg_slots AS sl ON sl.definition_id = s.definition_id;

-- A schedule whose timing changed is retimed: the old stream's undecided prefix is discarded, so the watermark moves to
-- now and never back; the plan's cursor is the new timing's first occurrence. Written before the declaration, which
-- overwrites the fingerprint the retime is decided by.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    107 /* EventCode.ScheduleRetimed */, now(), @p_namespace_id,
    50 /* EntityCode.Job */, s.job_id,
    s.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    1 /* JobPayloadFormat.Json */, convert_to('{"schedule":"' || s.schedule_name || '"}', 'UTF8')
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
INNER JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id
INNER JOIN {{schema}}.jobs_state AS r ON r.job_id = s.job_id
WHERE s.timing_fingerprint <> src.timing_fingerprint AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

-- A schedule with no slot records its retime against its definition.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id,
    details_format_id, details
)
SELECT
    107 /* EventCode.ScheduleRetimed */, now(), @p_namespace_id,
    30 /* EntityCode.Definition */, s.definition_id,
    s.definition_id,
    1 /* JobPayloadFormat.Json */, convert_to('{"schedule":"' || s.schedule_name || '"}', 'UTF8')
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
INNER JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
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
    modified_at_utc = now()
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
WHERE
    st.schedule_id = s.schedule_id
    AND (
        st.next_occurrence_at_utc IS DISTINCT FROM src.next_occurrence_at_utc
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
    calendar_origin_code = src.calendar_origin_code,
    calendar_version = src.calendar_version,
    availability_calendar_name = src.availability_calendar_name,
    availability_calendar_origin_code = src.availability_calendar_origin_code,
    availability_calendar_version = src.availability_calendar_version,
    schedule_status_code = CASE WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        THEN 10 /* ScheduleStatusCode.Active */ ELSE tgt.schedule_status_code END,
    description = src.description,
    modified_at_utc = now(),
    version = tgt.version + 1
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules AS cur ON cur.definition_id = src.definition_id AND cur.schedule_name = src.schedule_name
INNER JOIN {{schema}}.schedules_state AS st ON st.schedule_id = cur.schedule_id
WHERE
    tgt.schedule_id = cur.schedule_id
    AND (
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR tgt.expression IS DISTINCT FROM src.expression
        OR tgt.time_zone_id IS DISTINCT FROM src.time_zone_id
        OR tgt.expression_kind_code IS DISTINCT FROM src.expression_kind_code
        OR tgt.misfire_strategy_code IS DISTINCT FROM src.misfire_strategy_code
        OR tgt.timing_fingerprint IS DISTINCT FROM src.timing_fingerprint
        OR tgt.max_outstanding IS DISTINCT FROM src.max_outstanding
        OR tgt.full_behavior_code IS DISTINCT FROM src.full_behavior_code
        OR tgt.expire_after_seconds IS DISTINCT FROM src.expire_after_seconds
        OR tgt.expire_action_code IS DISTINCT FROM src.expire_action_code
        OR tgt.calendar_origin_code IS DISTINCT FROM src.calendar_origin_code
        OR tgt.calendar_version IS DISTINCT FROM src.calendar_version
        OR tgt.availability_calendar_name IS DISTINCT FROM src.availability_calendar_name
        OR tgt.availability_calendar_origin_code IS DISTINCT FROM src.availability_calendar_origin_code
        OR tgt.availability_calendar_version IS DISTINCT FROM src.availability_calendar_version
        OR st.next_occurrence_at_utc IS DISTINCT FROM src.next_occurrence_at_utc
        OR tgt.description IS DISTINCT FROM src.description
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
    calendar_origin_code,
    calendar_version,
    availability_calendar_name,
    availability_calendar_origin_code,
    availability_calendar_version,
    schedule_status_code,
    paused_until_utc,
    description,
    created_at_utc,
    modified_at_utc,
    version)
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
    src.calendar_origin_code,
    src.calendar_version,
    src.availability_calendar_name,
    src.availability_calendar_origin_code,
    src.availability_calendar_version,
    10 /* ScheduleStatusCode.Active */,
    NULL,
    src.description,
    now(),
    now(),
    0
FROM _reg_schedules AS src
ON CONFLICT (definition_id, schedule_name) DO NOTHING;

INSERT INTO {{schema}}.schedules_state (
    schedule_id,
    namespace_id,
    next_occurrence_at_utc,
    next_evaluation_at_utc,
    evaluation_status_code,
    outstanding_count,
    version,
    modified_at_utc)
SELECT
    s.schedule_id,
    @p_namespace_id,
    src.next_occurrence_at_utc,
    src.next_occurrence_at_utc,
    CASE WHEN src.next_occurrence_at_utc IS NULL THEN 30 /* EvaluationStatusCode.Exhausted */ ELSE 10 /* EvaluationStatusCode.Scheduled */ END,
    0,
    0,
    now()
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules AS s ON s.definition_id = src.definition_id AND s.schedule_name = src.schedule_name
ON CONFLICT (schedule_id) DO NOTHING;

WITH orphaned AS (
    UPDATE {{schema}}.schedules AS js
    SET
        schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
        paused_until_utc = NULL,
        pause_message = NULL,
        modified_at_utc = now(),
        version = js.version + 1
    WHERE
        js.definition_id = ANY(@p_d_definition_id)
        AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
        AND NOT EXISTS (
            SELECT 1
            FROM _reg_schedules AS src
            WHERE
                src.definition_id = js.definition_id
                AND src.schedule_name = js.schedule_name
        )
    RETURNING js.schedule_id
)
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,

    next_evaluation_at_utc = NULL,

    evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

    version = st.version + 1,
    modified_at_utc = now()
FROM orphaned AS o
WHERE st.schedule_id = o.schedule_id;

-- A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
-- retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace.
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,

    next_evaluation_at_utc = NULL,

    evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

    version = st.version + 1,
    modified_at_utc = now()
FROM {{schema}}.schedules AS s
INNER JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
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
    AND catalog_hash IS DISTINCT FROM @p_catalog_hash;

SELECT r.definition_id, r.slot_id FROM _reg_slots AS r;
