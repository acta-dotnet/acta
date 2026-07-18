CREATE OR REPLACE FUNCTION {{schema}}.checkpoint_slot(
    p_action           SMALLINT,
    p_job_id           BIGINT,
    p_kind_code        SMALLINT,
    p_name             VARCHAR,
    p_value_format_id  SMALLINT,
    p_value            BYTEA
)
RETURNS TABLE (found INT, value_format_id SMALLINT, value BYTEA, version INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
    v_deleted INTEGER;
BEGIN
    IF p_action IN (10 /* CheckpointSlotAction.Set */, 30 /* CheckpointSlotAction.GetOrSet */)
       AND (p_value_format_id = 0 /* JobPayloadFormat.None */ OR p_value IS NULL) THEN
        RAISE EXCEPTION 'Variable payload must use a non-zero format id and non-NULL value.';
    END IF;

    IF p_action = 10 /* CheckpointSlotAction.Set */ THEN
        INSERT INTO {{schema}}.checkpoints (
            job_id, kind_code, name,
            value_format_id, value,
            created_at_utc, modified_at_utc, version)
        VALUES (
            p_job_id, p_kind_code, p_name,
            p_value_format_id, p_value,
            now(), now(), 0)
        ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
            value_format_id = EXCLUDED.value_format_id,
            value = EXCLUDED.value,
            modified_at_utc = now(),
            version = {{schema}}.checkpoints.version + 1;

        RETURN QUERY SELECT 1, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSIF p_action = 20 /* CheckpointSlotAction.Get */ THEN
        RETURN QUERY
        SELECT 1, jv.value_format_id, jv.value, jv.version
          FROM {{schema}}.checkpoints AS jv
         WHERE jv.job_id = p_job_id
           AND jv.kind_code = p_kind_code
           AND jv.name = p_name;

        IF NOT FOUND THEN
            RETURN QUERY SELECT 0, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
        END IF;
    ELSIF p_action = 30 /* CheckpointSlotAction.GetOrSet */ THEN
        LOOP
            RETURN QUERY
            SELECT 1, jv.value_format_id, jv.value, jv.version
              FROM {{schema}}.checkpoints AS jv
             WHERE jv.job_id = p_job_id
               AND jv.kind_code = p_kind_code
               AND jv.name = p_name;

            IF FOUND THEN
                RETURN;
            END IF;

            RETURN QUERY
            INSERT INTO {{schema}}.checkpoints AS inserted (
                job_id, kind_code, name,
                value_format_id, value,
                created_at_utc, modified_at_utc, version)
            VALUES (
                p_job_id, p_kind_code, p_name,
                p_value_format_id, p_value,
                now(), now(), 0)
            ON CONFLICT (job_id, kind_code, name) DO NOTHING
            RETURNING 1, inserted.value_format_id, inserted.value, inserted.version;

            IF FOUND THEN
                RETURN;
            END IF;
        END LOOP;
    ELSIF p_action = 40 /* CheckpointSlotAction.Exists */ THEN
        RETURN QUERY
        SELECT CASE WHEN EXISTS (
            SELECT 1
              FROM {{schema}}.checkpoints AS jv
             WHERE jv.job_id = p_job_id
               AND jv.kind_code = p_kind_code
               AND jv.name = p_name
        ) THEN 1 ELSE 0 END, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSIF p_action = 50 /* CheckpointSlotAction.Delete */ THEN
        DELETE FROM {{schema}}.checkpoints AS jv
         WHERE jv.job_id = p_job_id
           AND jv.kind_code = p_kind_code
           AND jv.name = p_name;

        GET DIAGNOSTICS v_deleted = ROW_COUNT;
        RETURN QUERY SELECT CASE WHEN v_deleted > 0 THEN 1 ELSE 0 END, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSE
        RAISE EXCEPTION 'checkpoint_slot received an unknown action.';
    END IF;
END;
$$;
