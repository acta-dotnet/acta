CREATE OR REPLACE FUNCTION {{schema}}.wait_signal(
    p_job_id BIGINT,
    p_kind_code SMALLINT,
    p_name VARCHAR
)
RETURNS TABLE (
    outcome_code SMALLINT,
    value_format_id SMALLINT,
    value BYTEA
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_state SMALLINT;
    v_fmt SMALLINT;
    v_val BYTEA;
BEGIN
    SELECT js.status_code, js.value_format_id, js.value
    INTO v_state, v_fmt, v_val
    FROM {{schema}}.checkpoints js
    WHERE js.job_id = p_job_id AND js.kind_code = p_kind_code AND js.name = p_name
    FOR UPDATE;

    IF v_state = 20 /* JobCheckpointStatusCode.Set */ THEN
        RETURN QUERY SELECT
            2 /* SignalWaitOutcomeCode.ContinueSet */::SMALLINT,
            v_fmt,
            v_val;
    ELSE
        INSERT INTO {{schema}}.checkpoints (
            job_id,
            kind_code,
            name,
            status_code,
            value_format_id,
            value,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_job_id,
            p_kind_code,
            p_name,
            10 /* JobCheckpointStatusCode.Pending */,
            0 /* JobPayloadFormat.None */,
            NULL,
            v_now,
            v_now,
            0)
        ON CONFLICT (job_id, kind_code, name) DO NOTHING;

        RETURN QUERY SELECT
            1 /* SignalWaitOutcomeCode.SuspendPending */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    END IF;
END;
$$;
