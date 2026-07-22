CREATE OR REPLACE FUNCTION {{schema}}.register_scheduled_jobs(
    p_namespace_id       SMALLINT,
    p_d_job_ref              UUID[],
    p_d_definition_id    INT[],
    p_d_deduplication_key           VARCHAR[],
    p_d_input_format_id      SMALLINT[],
    p_d_input                BYTEA[],
    p_d_audit_level          SMALLINT[],
    p_d_slot_status          SMALLINT[],
    p_d_slot_next_run_at_utc TIMESTAMPTZ[],
    p_s_definition_id    INT[],
    p_s_name                 VARCHAR[],
    p_s_expression           VARCHAR[],
    p_s_time_zone            VARCHAR[],
    p_s_expression_kind      SMALLINT[],
    p_s_misfire             SMALLINT[],
    p_s_next_run_at_utc      TIMESTAMPTZ[],
    p_s_description          VARCHAR[]
)
RETURNS TABLE(out_definition_id INT, out_slot_id BIGINT)
LANGUAGE plpgsql
AS $$
BEGIN
    CREATE TEMP TABLE _reg_slots (definition_id INT PRIMARY KEY, slot_id BIGINT NOT NULL) ON COMMIT DROP;

    WITH defs AS (
        SELECT *
          FROM unnest(
              p_d_job_ref, p_d_definition_id, p_d_deduplication_key, p_d_input_format_id, p_d_input,
              p_d_audit_level, p_d_slot_status, p_d_slot_next_run_at_utc)
          AS d(job_ref, definition_id, deduplication_key, input_format_id, input, audit_level, slot_status, slot_next_run)
    ),
    upserted AS (
        INSERT INTO {{schema}}.jobs (
            job_ref, lineage_root_id, parent_id, deduplication_key, correlation_key,
            namespace_id, definition_id,
            input_format_id, input,
            exclusive_key, audit_level_code,
            created_at_utc)
        SELECT
            d.job_ref, NULL, NULL, d.deduplication_key, NULL,
            p_namespace_id, d.definition_id,
            d.input_format_id, d.input,
            NULL, d.audit_level,
            now()
          FROM defs AS d
         ORDER BY d.deduplication_key
        ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_id IS NULL
        DO UPDATE SET
            input_format_id  = EXCLUDED.input_format_id,
            input            = EXCLUDED.input,
            audit_level_code = EXCLUDED.audit_level_code
        RETURNING {{schema}}.jobs.definition_id, {{schema}}.jobs.id
    )
    INSERT INTO _reg_slots (definition_id, slot_id)
    SELECT definition_id, id FROM upserted;

    INSERT INTO {{schema}}.runtimes (
        job_id, namespace_id, status_code, priority_code, next_run_at_utc,
        execution_number, failure_count, retention_until_utc,
        modified_at_utc, version)
    SELECT
        sl.slot_id, p_namespace_id, d.slot_status, jd.priority_code_effective, d.slot_next_run,
        0, 0, NULL,
        now(), 0
      FROM unnest(
          p_d_definition_id, p_d_slot_status, p_d_slot_next_run_at_utc)
        AS d(definition_id, slot_status, slot_next_run)
      INNER JOIN _reg_slots AS sl ON sl.definition_id = d.definition_id
      INNER JOIN {{schema}}.definitions AS jd ON jd.id = d.definition_id
    -- Re-registration re-asserts the definition's declared priority onto the slot, overwriting any operator reprioritize.
    ON CONFLICT (job_id) DO UPDATE SET
        status_code     = EXCLUDED.status_code,
        priority_code   = EXCLUDED.priority_code,
        next_run_at_utc = EXCLUDED.next_run_at_utc,
        modified_at_utc = now(),
        version         = {{schema}}.runtimes.version + 1;

    INSERT INTO {{schema}}.schedules (
        namespace_id, job_id, definition_id, name, origin_code,
        expression, time_zone_id, expression_kind_code, misfire_strategy_code,
        next_run_at_utc, expression_override, time_zone_id_override, orphaned_at_utc,
        status_code, paused_until_utc, description,
        created_at_utc, modified_at_utc, version)
    SELECT
        p_namespace_id, sl.slot_id, s.definition_id, s.name, 40 /* ScheduleOriginCode.Definition */,
        s.expression, s.time_zone, s.expression_kind, s.misfire,
        s.next_run, NULL, NULL, NULL,
        10 /* ScheduleStatusCode.Active */, NULL, s.description,
        now(), now(), 0
      FROM unnest(
          p_s_definition_id, p_s_name, p_s_expression, p_s_time_zone,
          p_s_expression_kind, p_s_misfire, p_s_next_run_at_utc, p_s_description)
        AS s(definition_id, name, expression, time_zone, expression_kind, misfire, next_run, description)
      INNER JOIN _reg_slots AS sl ON sl.definition_id = s.definition_id
    ON CONFLICT (job_id, name) DO UPDATE SET
        expression           = EXCLUDED.expression,
        time_zone_id            = EXCLUDED.time_zone_id,
        expression_kind_code = EXCLUDED.expression_kind_code,
        misfire_strategy_code         = EXCLUDED.misfire_strategy_code,
        next_run_at_utc               = EXCLUDED.next_run_at_utc,
        definition_id             = EXCLUDED.definition_id,
        orphaned_at_utc               = NULL,
        status_code                   = CASE WHEN {{schema}}.schedules.status_code = 230 /* ScheduleStatusCode.Orphaned */
                                             THEN 10 /* ScheduleStatusCode.Active */
                                             ELSE {{schema}}.schedules.status_code END,
        description                   = EXCLUDED.description,
        modified_at_utc               = now(),
        version                       = {{schema}}.schedules.version + 1;

    UPDATE {{schema}}.schedules AS js
       SET orphaned_at_utc = now(),
           status_code     = 230 /* ScheduleStatusCode.Orphaned */,
           modified_at_utc = now(),
           version         = js.version + 1
      FROM _reg_slots AS sl
     WHERE js.job_id = sl.slot_id
       AND js.orphaned_at_utc IS NULL
       AND NOT EXISTS (
           SELECT 1
             FROM unnest(p_s_definition_id, p_s_name) AS s(definition_id, name)
            WHERE s.definition_id = sl.definition_id
              AND s.name = js.name
       );

    RETURN QUERY SELECT r.definition_id, r.slot_id FROM _reg_slots AS r;
END;
$$;
