CREATE OR REPLACE FUNCTION {{schema}}.start_step(
    p_job_id BIGINT,
    p_execution_number INT,
    p_leased_by_worker_id INT,
    p_step_name VARCHAR,
    p_at_most_once BOOLEAN
)
RETURNS TABLE (
    outcome_code SMALLINT,
    attempt_number INT,
    version INT,
    next_retry_at_utc TIMESTAMPTZ,
    result_format_id SMALLINT,
    result BYTEA,
    failure_code SMALLINT,
    failure_message VARCHAR
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_state SMALLINT;
    v_attempt INT;
    v_version INT;
    v_next TIMESTAMPTZ;
    v_rfid SMALLINT;
    v_result BYTEA;
    v_rcode SMALLINT;
    v_rmsg VARCHAR;
BEGIN
    -- The step changes only while this attempt holds the job's row, share-locked to commit before the step row
    -- (docs/sql-internals.md, "Execution fence").
    PERFORM 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = p_execution_number
        AND r.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
    FOR SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:start_step: job % is no longer executing attempt %.', p_job_id, p_execution_number
            USING ERRCODE = 'P0001';
    END IF;

    SELECT
        a.step_status_code,
        a.attempt_number,
        a.version,
        a.next_retry_at_utc,
        a.result_format_id,
        a.result,
        a.failure_code,
        a.failure_message
    INTO v_state, v_attempt, v_version, v_next, v_rfid, v_result, v_rcode, v_rmsg
    FROM {{schema}}.steps a
    WHERE
        a.job_id = p_job_id
        AND a.step_name = p_step_name
    FOR UPDATE;

    IF NOT FOUND THEN
        INSERT INTO {{schema}}.steps (
            job_id,
            step_name,
            step_status_code,
            attempt_number,
            result_format_id,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_job_id,
            p_step_name,
            10 /* StepStatusCode.Pending */,
            1,
            0 /* JobPayloadFormat.None */,
            v_now,
            v_now,
            0)
        ON CONFLICT (job_id, step_name) DO NOTHING
        RETURNING steps.attempt_number, steps.version
        INTO v_attempt, v_version;

        IF FOUND THEN
            RETURN QUERY SELECT 1 /* StartStepOutcomeCode.Invoke */::SMALLINT, v_attempt, v_version,
                NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
            RETURN;
        END IF;

        -- A concurrent first start inserted the slot first: read it as a replay would, as SQL Server's step lock
        -- queues this caller behind that insert.
        SELECT
            a.step_status_code,
            a.attempt_number,
            a.version,
            a.next_retry_at_utc,
            a.result_format_id,
            a.result,
            a.failure_code,
            a.failure_message
        INTO v_state, v_attempt, v_version, v_next, v_rfid, v_result, v_rcode, v_rmsg
        FROM {{schema}}.steps a
        WHERE
            a.job_id = p_job_id
            AND a.step_name = p_step_name
        FOR UPDATE;
    END IF;

    IF v_state = 100 /* StepStatusCode.Succeeded */ THEN

        RETURN QUERY SELECT 3 /* StartStepOutcomeCode.ReplaySuccess */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, v_rfid, v_result, NULL::SMALLINT, NULL::VARCHAR;
    ELSIF v_state = 200 /* StepStatusCode.Exhausted */ THEN

        RETURN QUERY SELECT 4 /* StartStepOutcomeCode.Exhausted */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, v_rcode, v_rmsg;
    ELSIF v_state = 230 /* StepStatusCode.Interrupted */ THEN
        -- Terminal at-most-once ambiguity from an earlier replay; re-throw consistently, no mutation.
        RETURN QUERY SELECT 5 /* StartStepOutcomeCode.Interrupted */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    ELSIF v_next IS NOT NULL AND v_next > v_now THEN

        RETURN QUERY SELECT 2 /* StartStepOutcomeCode.Suspend */::SMALLINT, v_attempt, v_version,
            v_next, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    ELSIF p_at_most_once THEN
        -- Pending slot re-entered on replay under AtMostOnce: the worker died after start_step recorded
        -- the pending row but before complete_step. Do not re-invoke; terminalize the row Interrupted
        -- (one transition, one version bump) and let the orchestration throw StepInterruptedException.
        UPDATE {{schema}}.steps a
        SET
            step_status_code = 230 /* StepStatusCode.Interrupted */,
            next_retry_at_utc = NULL,
            failure_code = 20 /* StepFailureCode.Interrupted */,
            failure_message = 'At-most-once step re-entered before completion; outcome unknown.',
            modified_at_utc = v_now,
            version = a.version + 1
        WHERE
            a.job_id = p_job_id
            AND a.step_name = p_step_name
        RETURNING a.version INTO v_version;

        RETURN QUERY SELECT 5 /* StartStepOutcomeCode.Interrupted */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    ELSE
        UPDATE {{schema}}.steps a
        SET
            attempt_number = a.attempt_number + 1,
            modified_at_utc = v_now,
            version = a.version + 1
        WHERE
            a.job_id = p_job_id
            AND a.step_name = p_step_name
        RETURNING a.attempt_number, a.version INTO v_attempt, v_version;

        RETURN QUERY SELECT 1 /* StartStepOutcomeCode.Invoke */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    END IF;
END;
$$;
