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
        @p_d_job_ref, @p_d_definition_id, @p_d_deduplication_key, @p_d_input_format_id, @p_d_input,
        @p_d_event_recording_code, @p_d_slot_status, @p_d_slot_next_run_at_utc)
    AS d(job_ref, definition_id, deduplication_key, input_format_id, input, event_recording_code, slot_status, slot_next_run)
),
upserted AS (
    INSERT INTO {{schema}}.jobs (
        job_ref,
        lineage_root_job_id,
        parent_job_id,
        deduplication_key,
        correlation_key,
        namespace_id,
        definition_id,
        input_format_id,
        input,
        concurrency_key,
        event_recording_code,
        created_at_utc)
    SELECT
        d.job_ref,
        NULL,
        NULL,
        d.deduplication_key,
        NULL,
        @p_namespace_id,
        d.definition_id,
        d.input_format_id,
        d.input,
        NULL,
        d.event_recording_code,
        now()
    FROM defs AS d
    ORDER BY d.deduplication_key
    ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL
    DO UPDATE SET
        input_format_id = EXCLUDED.input_format_id,
        input = EXCLUDED.input,
        event_recording_code = EXCLUDED.event_recording_code
    -- A row already holding the key is the slot only when it owns schedules; an ordinary job that took
    -- the key is left untouched and missing from the result, which fails the worker's startup.
    WHERE EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = {{schema}}.jobs.job_id)
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
    d.slot_status,
    jd.job_priority_code_effective,
    d.slot_next_run,
    0,
    0,
    NULL,
    now(),
    0
FROM unnest(
    @p_d_definition_id, @p_d_slot_status, @p_d_slot_next_run_at_utc)
AS d(definition_id, slot_status, slot_next_run)
INNER JOIN _reg_slots AS sl ON sl.definition_id = d.definition_id
INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = d.definition_id
-- The slot rows lock in key order: a completion that settles a child locks its slot parent's row
-- without the schedule lock.
ORDER BY sl.slot_id
ON CONFLICT (job_id) DO UPDATE SET
    job_status_code = EXCLUDED.job_status_code,
    job_priority_code = EXCLUDED.job_priority_code,
    next_run_at_utc = EXCLUDED.next_run_at_utc,
    modified_at_utc = now(),
    version = {{schema}}.jobs_state.version + 1
WHERE
    -- An attempt in flight owns the slot, and a finished slot stays finished until an operator restarts it.
    {{schema}}.jobs_state.job_status_code NOT IN (
        50 /* JobStatusCode.Executing */,
        100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    -- A held job (Paused with a next run; ScheduleWalker, "held") stays as its operator left it.
    AND NOT ({{schema}}.jobs_state.job_status_code = 30 /* JobStatusCode.Paused */ AND {{schema}}.jobs_state.next_run_at_utc IS NOT NULL)
    -- A wait is left to the signal, child, or deadline that ends it, and a start never parks a slot: one a
    -- signal woke after the start read it would otherwise go back to sleep.
    AND {{schema}}.jobs_state.job_status_code <> 20 /* JobStatusCode.Suspended */
    AND EXCLUDED.job_status_code <> 20 /* JobStatusCode.Suspended */
    -- An unchanged declaration writes nothing: a restart of the same build bumps no version.
    AND (
        {{schema}}.jobs_state.job_status_code IS DISTINCT FROM EXCLUDED.job_status_code
        OR {{schema}}.jobs_state.job_priority_code IS DISTINCT FROM EXCLUDED.job_priority_code
        OR {{schema}}.jobs_state.next_run_at_utc IS DISTINCT FROM EXCLUDED.next_run_at_utc
    );

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
    s.description
FROM unnest(
    @p_s_definition_id, @p_s_schedule_name, @p_s_expression, @p_s_time_zone,
    @p_s_expression_kind, @p_s_misfire, @p_s_next_occurrence_at_utc, @p_s_description)
AS s(
    definition_id, schedule_name, expression, time_zone_id,
    expression_kind_code, misfire_strategy_code, next_occurrence_at_utc, description)
INNER JOIN _reg_slots AS sl ON sl.definition_id = s.definition_id;

UPDATE {{schema}}.schedules AS tgt
SET
    expression = src.expression,
    time_zone_id = src.time_zone_id,
    expression_kind_code = src.expression_kind_code,
    -- An override was validated against the kind it was set for; one left under the other kind cannot parse.
    expression_override = CASE WHEN tgt.expression_kind_code <> src.expression_kind_code
        THEN NULL ELSE tgt.expression_override END,
    misfire_strategy_code = src.misfire_strategy_code,
    definition_id = src.definition_id,
    schedule_status_code = CASE WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        THEN 10 /* ScheduleStatusCode.Active */ ELSE tgt.schedule_status_code END,
    description = src.description,
    modified_at_utc = now(),
    version = tgt.version + 1
FROM _reg_schedules AS src
INNER JOIN {{schema}}.schedules_state AS st ON st.job_id = src.job_id AND st.schedule_name = src.schedule_name
WHERE
    tgt.job_id = src.job_id
    AND tgt.schedule_name = src.schedule_name
    AND (
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR tgt.expression IS DISTINCT FROM src.expression
        OR tgt.time_zone_id IS DISTINCT FROM src.time_zone_id
        OR tgt.expression_kind_code IS DISTINCT FROM src.expression_kind_code
        OR tgt.misfire_strategy_code IS DISTINCT FROM src.misfire_strategy_code
        OR st.next_occurrence_at_utc IS DISTINCT FROM src.next_occurrence_at_utc
        OR tgt.definition_id IS DISTINCT FROM src.definition_id
        OR tgt.description IS DISTINCT FROM src.description
    );

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = src.next_occurrence_at_utc,
    modified_at_utc = now()
FROM _reg_schedules AS src
WHERE
    st.job_id = src.job_id
    AND st.schedule_name = src.schedule_name
    AND st.next_occurrence_at_utc IS DISTINCT FROM src.next_occurrence_at_utc;

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
    expression_override,
    time_zone_id_override,
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
    NULL,
    NULL,
    10 /* ScheduleStatusCode.Active */,
    NULL,
    src.description,
    now(),
    now(),
    0
FROM _reg_schedules AS src
ON CONFLICT (job_id, schedule_name) DO NOTHING;

INSERT INTO {{schema}}.schedules_state (
    job_id,
    schedule_name,
    namespace_id,
    next_occurrence_at_utc,
    last_advanced_occurrence_at_utc,
    modified_at_utc)
SELECT
    src.job_id,
    src.schedule_name,
    @p_namespace_id,
    src.next_occurrence_at_utc,
    NULL,
    now()
FROM _reg_schedules AS src
ON CONFLICT (job_id, schedule_name) DO NOTHING;

WITH orphaned AS (
    UPDATE {{schema}}.schedules AS js
    SET
        schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
        paused_until_utc = NULL,
        modified_at_utc = now(),
        version = js.version + 1
    FROM _reg_slots AS sl
    WHERE
        js.job_id = sl.slot_id
        AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
        AND NOT EXISTS (
            SELECT 1
            FROM _reg_schedules AS src
            WHERE
                src.job_id = js.job_id
                AND src.schedule_name = js.schedule_name
        )
    RETURNING js.job_id, js.schedule_name
)
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,
    last_advanced_occurrence_at_utc = NULL,
    modified_at_utc = now()
FROM orphaned AS o
WHERE
    st.job_id = o.job_id
    AND st.schedule_name = o.schedule_name;

-- A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
-- retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace.
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,
    last_advanced_occurrence_at_utc = NULL,
    modified_at_utc = now()
FROM {{schema}}.schedules AS s
INNER JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
WHERE
    st.job_id = s.job_id
    AND st.schedule_name = s.schedule_name
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
