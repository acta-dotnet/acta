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
    event_recording_code)
SELECT
    json_extract(d.value, '$.job_ref'),
    NULL,
    NULL,
    json_extract(d.value, '$.deduplication_key'),
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
ORDER BY json_extract(d.value, '$.deduplication_key')
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL
DO UPDATE SET
    input_format_id = excluded.input_format_id,
    input = excluded.input,
    event_recording_code = excluded.event_recording_code
-- A row already holding the key is the slot only when it owns schedules; an ordinary job that took the
-- key is left untouched and missing from the result, which fails the worker's startup.
WHERE EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = {{schema}}.jobs.job_id);

DROP TABLE IF EXISTS temp._reg_slots;

-- The slot this call inserted, or one that already owns schedules.
CREATE TEMP TABLE _reg_slots AS
SELECT json_extract(d.value, '$.definition_id') AS definition_id, j.job_id AS slot_id
FROM json_each(@p_definitions) d
JOIN {{schema}}.jobs j
    ON j.namespace_id = @p_namespace_id
    AND j.parent_job_id IS NULL
    AND j.deduplication_key = json_extract(d.value, '$.deduplication_key')
WHERE
    j.job_ref = json_extract(d.value, '$.job_ref')
    OR EXISTS (SELECT 1 FROM {{schema}}.schedules AS s WHERE s.job_id = j.job_id);

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
    json_extract(d.value, '$.slot_status_code'),
    jd.job_priority_code_effective,
    json_extract(d.value, '$.slot_next_run_at_utc'),
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
    job_status_code = excluded.job_status_code,
    job_priority_code = excluded.job_priority_code,
    next_run_at_utc = excluded.next_run_at_utc,
    modified_at_utc = {{now}},
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
    AND excluded.job_status_code <> 20 /* JobStatusCode.Suspended */
    -- An unchanged declaration writes nothing: a restart of the same build bumps no version.
    AND (
        {{schema}}.jobs_state.job_status_code IS NOT excluded.job_status_code
        OR {{schema}}.jobs_state.job_priority_code IS NOT excluded.job_priority_code
        OR {{schema}}.jobs_state.next_run_at_utc IS NOT excluded.next_run_at_utc
    );

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
    json_extract(s.value, '$.description') AS description
FROM json_each(@p_schedules) s
JOIN temp._reg_slots sl ON sl.definition_id = json_extract(s.value, '$.definition_id');

UPDATE {{schema}}.schedules AS tgt
SET
    expression = src.expression,
    time_zone_id = src.time_zone_id,
    expression_kind_code = src.expression_kind_code,
    -- An override was validated against the kind it was set for; one left under the other kind cannot parse.
    expression_override = CASE WHEN tgt.expression_kind_code IS NOT src.expression_kind_code
        THEN NULL
        ELSE tgt.expression_override END,
    misfire_strategy_code = src.misfire_strategy_code,
    definition_id = src.definition_id,
    schedule_status_code = CASE WHEN tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        THEN 10 /* ScheduleStatusCode.Active */
        ELSE tgt.schedule_status_code END,
    description = src.description,
    modified_at_utc = {{now}},
    version = tgt.version + 1
FROM temp._reg_schedules src
JOIN {{schema}}.schedules_state st ON st.job_id = src.job_id AND st.schedule_name = src.schedule_name
WHERE
    tgt.job_id = src.job_id
    AND tgt.schedule_name = src.schedule_name
    AND (
        tgt.schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */
        OR tgt.expression IS NOT src.expression
        OR tgt.time_zone_id IS NOT src.time_zone_id
        OR tgt.expression_kind_code IS NOT src.expression_kind_code
        OR tgt.misfire_strategy_code IS NOT src.misfire_strategy_code
        OR st.next_occurrence_at_utc IS NOT src.next_occurrence_at_utc
        OR tgt.definition_id IS NOT src.definition_id
        OR tgt.description IS NOT src.description
    );

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = src.next_occurrence_at_utc,
    modified_at_utc = {{now}}
FROM temp._reg_schedules src
WHERE
    st.job_id = src.job_id
    AND st.schedule_name = src.schedule_name
    AND st.next_occurrence_at_utc IS NOT src.next_occurrence_at_utc;

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
    NULL,
    NULL,
    10 /* ScheduleStatusCode.Active */,
    NULL,
    src.description
FROM temp._reg_schedules src
WHERE true
ON CONFLICT (job_id, schedule_name) DO NOTHING;

INSERT INTO {{schema}}.schedules_state (
    job_id,
    schedule_name,
    namespace_id,
    next_occurrence_at_utc,
    last_advanced_occurrence_at_utc)
SELECT
    src.job_id,
    src.schedule_name,
    @p_namespace_id,
    src.next_occurrence_at_utc,
    NULL
FROM temp._reg_schedules src
WHERE true
ON CONFLICT (job_id, schedule_name) DO NOTHING;

DROP TABLE IF EXISTS temp._reg_orphaned;

CREATE TEMP TABLE _reg_orphaned AS
SELECT js.job_id, js.schedule_name
FROM {{schema}}.schedules js
JOIN temp._reg_slots sl ON sl.slot_id = js.job_id
WHERE
    js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND js.schedule_origin_code = 40 /* ScheduleOriginCode.Definition */
    AND NOT EXISTS (
        SELECT 1
        FROM temp._reg_schedules src
        WHERE
            src.job_id = js.job_id
            AND src.schedule_name = js.schedule_name
    );

UPDATE {{schema}}.schedules AS js
SET
    schedule_status_code = 230 /* ScheduleStatusCode.Orphaned */,
    paused_until_utc = NULL,
    modified_at_utc = {{now}},
    version = js.version + 1
FROM temp._reg_orphaned o
WHERE
    js.job_id = o.job_id
    AND js.schedule_name = o.schedule_name;

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,
    last_advanced_occurrence_at_utc = NULL,
    modified_at_utc = {{now}}
FROM temp._reg_orphaned o
WHERE
    st.job_id = o.job_id
    AND st.schedule_name = o.schedule_name;

-- A retire that commits after the worker read its stored schedules leaves this call writing cursors for the
-- retired definition's schedules; the heal clears every orphaned or retired schedule's cursor in the namespace.
UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,
    last_advanced_occurrence_at_utc = NULL,
    modified_at_utc = {{now}}
FROM {{schema}}.schedules s
JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
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
    AND catalog_hash IS NOT @p_catalog_hash;

SELECT definition_id, slot_id FROM temp._reg_slots;
