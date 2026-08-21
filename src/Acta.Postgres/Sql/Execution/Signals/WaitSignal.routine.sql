/* Slot-locked arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a timeout, so code
   redeployed with a bound can un-strand a wait suspended without one; a stored due is never
   overwritten, never extended, and never cleared by a subsequent unbounded call. */
CREATE OR REPLACE FUNCTION {{schema}}.wait_signal(
    p_job_id BIGINT,
    p_kind_code SMALLINT,
    p_name VARCHAR,
    p_timeout_seconds INT DEFAULT NULL
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
    v_due TIMESTAMPTZ;
BEGIN
    SELECT js.status_code, js.value_format_id, js.value, js.due_at_utc
    INTO v_state, v_fmt, v_val, v_due
    FROM {{schema}}.checkpoints js
    WHERE js.job_id = p_job_id AND js.kind_code = p_kind_code AND js.name = p_name
    FOR UPDATE;

    IF v_state = 20 /* JobCheckpointStatusCode.Set */ THEN
        RETURN QUERY SELECT
            2 /* SignalWaitOutcomeCode.ContinueSet */::SMALLINT,
            v_fmt,
            v_val;
    ELSIF v_state = 30 /* JobCheckpointStatusCode.Expired */ THEN
        RETURN QUERY SELECT
            3 /* SignalWaitOutcomeCode.TimedOut */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    ELSIF v_state = 10 /* JobCheckpointStatusCode.Pending */ AND v_due IS NOT NULL AND v_due <= v_now THEN

        UPDATE {{schema}}.checkpoints js
        SET
            status_code = 30 /* JobCheckpointStatusCode.Expired */,
            modified_at_utc = v_now,
            version = js.version + 1
        WHERE js.job_id = p_job_id AND js.kind_code = p_kind_code AND js.name = p_name;

        RETURN QUERY SELECT
            3 /* SignalWaitOutcomeCode.TimedOut */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    ELSE
        INSERT INTO {{schema}}.checkpoints (
            job_id,
            kind_code,
            name,
            status_code,
            due_at_utc,
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
            CASE WHEN p_timeout_seconds IS NULL THEN NULL ELSE v_now + make_interval(secs => p_timeout_seconds) END,
            0 /* JobPayloadFormat.None */,
            NULL,
            v_now,
            v_now,
            0)
        ON CONFLICT (job_id, kind_code, name) DO NOTHING;

        IF v_state = 10 /* JobCheckpointStatusCode.Pending */ AND v_due IS NULL AND p_timeout_seconds IS NOT NULL THEN

            UPDATE {{schema}}.checkpoints js
            SET
                due_at_utc = v_now + make_interval(secs => p_timeout_seconds),
                modified_at_utc = v_now,
                version = js.version + 1
            WHERE
                js.job_id = p_job_id
                AND js.kind_code = p_kind_code
                AND js.name = p_name
                AND js.status_code = 10 /* JobCheckpointStatusCode.Pending */
                AND js.due_at_utc IS NULL;
        END IF;

        RETURN QUERY SELECT
            1 /* SignalWaitOutcomeCode.SuspendPending */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    END IF;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- three-parameter signature so an upgraded install cannot resolve the unbounded-only form.
DROP FUNCTION IF EXISTS {{schema}}.wait_signal(BIGINT, SMALLINT, VARCHAR);
