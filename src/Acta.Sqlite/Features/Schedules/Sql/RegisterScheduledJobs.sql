INSERT INTO {{schema}}.jobs (
    job_ref, lineage_root_id, parent_id, deduplication_key, correlation_key,
    namespace_id, definition_id,
    input_format_id, input,
    exclusive_key, audit_level_code)
SELECT
    json_extract(d.value, '$.job_ref'), NULL, NULL, json_extract(d.value, '$.deduplication_key'), NULL,
    @p_namespace_id, json_extract(d.value, '$.definition_id'),
    json_extract(d.value, '$.input_format_id'), acta_blob(json_extract(d.value, '$.input')),
    NULL, json_extract(d.value, '$.audit_level_code')
  FROM json_each(@p_definitions) d
 ORDER BY json_extract(d.value, '$.deduplication_key')
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_id IS NULL
DO UPDATE SET
    input_format_id  = excluded.input_format_id,
    input            = excluded.input,
    audit_level_code = excluded.audit_level_code;

INSERT INTO {{schema}}.runtimes (
    job_id, namespace_id, status_code, priority_code, next_run_at_utc,
    execution_number, failure_count, retention_until_utc,
    modified_at_utc, version)
SELECT
    j.id, @p_namespace_id, json_extract(d.value, '$.slot_status_code'), 50 /* JobPriorityCode.Normal */,
    json_extract(d.value, '$.slot_next_run_at_utc'),
    0, 0, NULL,
    {{now}}, 0
  FROM json_each(@p_definitions) d
  JOIN {{schema}}.jobs j
    ON j.namespace_id = @p_namespace_id
   AND j.parent_id IS NULL
   AND j.deduplication_key = json_extract(d.value, '$.deduplication_key')
ON CONFLICT (job_id) DO UPDATE SET
    status_code     = excluded.status_code,
    next_run_at_utc = excluded.next_run_at_utc,
    modified_at_utc = {{now}},
    version         = {{schema}}.runtimes.version + 1;

DROP TABLE IF EXISTS temp._reg_slots;

CREATE TEMP TABLE _reg_slots AS
SELECT json_extract(d.value, '$.definition_id') AS definition_id, j.id AS slot_id
  FROM json_each(@p_definitions) d
  JOIN {{schema}}.jobs j
    ON j.namespace_id = @p_namespace_id
   AND j.parent_id IS NULL
   AND j.deduplication_key = json_extract(d.value, '$.deduplication_key');

INSERT INTO {{schema}}.schedules (
    namespace_id, job_id, definition_id, name, origin_code,
    expression, time_zone_id, expression_kind_code, misfire_strategy_code,
    next_run_at_utc, expression_override, time_zone_id_override, orphaned_at_utc,
    status_code, paused_until_utc, description)
SELECT
    @p_namespace_id, sl.slot_id, json_extract(s.value, '$.definition_id'), json_extract(s.value, '$.name'), 40 /* ScheduleOriginCode.Definition */,
    json_extract(s.value, '$.expression'), json_extract(s.value, '$.time_zone_id'),
    json_extract(s.value, '$.expression_kind_code'), json_extract(s.value, '$.misfire_strategy_code'),
    json_extract(s.value, '$.next_run_at_utc'), NULL, NULL, NULL,
    10 /* ScheduleStatusCode.Active */, NULL, json_extract(s.value, '$.description')
  FROM json_each(@p_schedules) s
  JOIN temp._reg_slots sl ON sl.definition_id = json_extract(s.value, '$.definition_id')
ON CONFLICT (job_id, name) DO UPDATE SET
    expression           = excluded.expression,
    time_zone_id            = excluded.time_zone_id,
    expression_kind_code = excluded.expression_kind_code,
    misfire_strategy_code         = excluded.misfire_strategy_code,
    next_run_at_utc               = excluded.next_run_at_utc,
    definition_id             = excluded.definition_id,
    orphaned_at_utc               = NULL,
    status_code                   = CASE WHEN {{schema}}.schedules.status_code = 230 /* ScheduleStatusCode.Orphaned */
                                         THEN 10 /* ScheduleStatusCode.Active */
                                         ELSE {{schema}}.schedules.status_code END,
    description                   = excluded.description,
    modified_at_utc               = {{now}},
    version                       = {{schema}}.schedules.version + 1;

UPDATE {{schema}}.schedules
   SET orphaned_at_utc = {{now}},
       status_code     = 230 /* ScheduleStatusCode.Orphaned */,
       modified_at_utc = {{now}},
       version         = version + 1
 WHERE job_id IN (SELECT slot_id FROM temp._reg_slots)
   AND orphaned_at_utc IS NULL
   AND NOT EXISTS (
       SELECT 1 FROM json_each(@p_schedules) s
        JOIN temp._reg_slots sl ON sl.definition_id = json_extract(s.value, '$.definition_id')
       WHERE sl.slot_id = {{schema}}.schedules.job_id
         AND json_extract(s.value, '$.name') = {{schema}}.schedules.name
   );

SELECT definition_id, slot_id FROM temp._reg_slots;
