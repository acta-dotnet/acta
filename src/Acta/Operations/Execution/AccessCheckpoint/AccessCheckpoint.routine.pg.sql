CREATE OR REPLACE FUNCTION {{schema}}.access_checkpoint(
    p_action SMALLINT,
    p_job_id BIGINT,
    p_execution_number INT,
    p_leased_by_worker_id INT,
    p_checkpoint_kind_code SMALLINT,
    p_checkpoint_name VARCHAR,
    p_value_format_id SMALLINT,
    p_value BYTEA
)
RETURNS TABLE (found INT, value_format_id SMALLINT, value BYTEA, version INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
    v_deleted INTEGER;
BEGIN
    IF p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */)
       AND (p_value_format_id = 0 /* JobPayloadFormat.None */ OR p_value IS NULL) THEN
        RAISE EXCEPTION 'Variable payload must use a non-zero format id and non-NULL value.';
    END IF;

    -- A write lands only while this attempt holds the job's row, which stays share-locked to commit
    -- (docs/sql-internals.md, "Execution fence"). Reads take no lock.
    IF p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */, 50 /* AccessCheckpointAction.Delete */) THEN
        PERFORM 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_id = p_job_id
            AND r.job_status_code = 50 /* JobStatusCode.Executing */
            AND r.execution_number = p_execution_number
            AND r.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
        FOR SHARE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:access_checkpoint: job % is no longer executing attempt %.', p_job_id, p_execution_number
                USING ERRCODE = 'P0001';
        END IF;
    END IF;

    IF p_action = 10 /* AccessCheckpointAction.Set */ THEN
        INSERT INTO {{schema}}.checkpoints (
            job_id,
            checkpoint_kind_code,
            checkpoint_name,
            value_format_id,
            value,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_job_id,
            p_checkpoint_kind_code,
            p_checkpoint_name,
            p_value_format_id,
            p_value,
            now(),
            now(),
            0)
        ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
            value_format_id = EXCLUDED.value_format_id,
            value = EXCLUDED.value,
            modified_at_utc = now(),
            version = {{schema}}.checkpoints.version + 1;

        RETURN QUERY SELECT 1, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSIF p_action = 20 /* AccessCheckpointAction.Get */ THEN
        RETURN QUERY
        SELECT 1, jv.value_format_id, jv.value, jv.version
        FROM {{schema}}.checkpoints AS jv
        WHERE
            jv.job_id = p_job_id
            AND jv.checkpoint_kind_code = p_checkpoint_kind_code
            AND jv.checkpoint_name = p_checkpoint_name;

        IF NOT FOUND THEN
            RETURN QUERY SELECT 0, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
        END IF;
    ELSIF p_action = 30 /* AccessCheckpointAction.GetOrSet */ THEN
        LOOP
            RETURN QUERY
            SELECT 1, jv.value_format_id, jv.value, jv.version
            FROM {{schema}}.checkpoints AS jv
            WHERE
                jv.job_id = p_job_id
                AND jv.checkpoint_kind_code = p_checkpoint_kind_code
                AND jv.checkpoint_name = p_checkpoint_name;

            IF FOUND THEN
                RETURN;
            END IF;

            RETURN QUERY
            INSERT INTO {{schema}}.checkpoints AS inserted (
                job_id,
                checkpoint_kind_code,
                checkpoint_name,
                value_format_id,
                value,
                created_at_utc,
                modified_at_utc,
                version)
            VALUES (
                p_job_id,
                p_checkpoint_kind_code,
                p_checkpoint_name,
                p_value_format_id,
                p_value,
                now(),
                now(),
                0)
            ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO NOTHING
            RETURNING 1, inserted.value_format_id, inserted.value, inserted.version;

            IF FOUND THEN
                RETURN;
            END IF;
        END LOOP;
    ELSIF p_action = 40 /* AccessCheckpointAction.Exists */ THEN
        RETURN QUERY
        SELECT CASE WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints AS jv
            WHERE
                jv.job_id = p_job_id
                AND jv.checkpoint_kind_code = p_checkpoint_kind_code
                AND jv.checkpoint_name = p_checkpoint_name
        ) THEN 1 ELSE 0 END, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSIF p_action = 50 /* AccessCheckpointAction.Delete */ THEN
        DELETE FROM {{schema}}.checkpoints AS jv
        WHERE
            jv.job_id = p_job_id
            AND jv.checkpoint_kind_code = p_checkpoint_kind_code
            AND jv.checkpoint_name = p_checkpoint_name;

        GET DIAGNOSTICS v_deleted = ROW_COUNT;
        RETURN QUERY SELECT CASE WHEN v_deleted > 0 THEN 1 ELSE 0 END, NULL::SMALLINT, NULL::BYTEA, NULL::INTEGER;
    ELSE
        RAISE EXCEPTION 'access_checkpoint received an unknown action.';
    END IF;
END;
$$;
