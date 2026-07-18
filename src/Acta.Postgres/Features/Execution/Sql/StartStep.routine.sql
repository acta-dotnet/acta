CREATE OR REPLACE FUNCTION {{schema}}.start_step(
    p_job_id       BIGINT,
    p_name         VARCHAR,
    p_at_most_once BOOLEAN
)
RETURNS TABLE (
    outcome_code      SMALLINT,
    attempt_number    SMALLINT,
    version           INT,
    next_retry_at_utc TIMESTAMPTZ,
    result_format_id  SMALLINT,
    result            BYTEA,
    reason_code       SMALLINT,
    reason_message    VARCHAR
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now     TIMESTAMPTZ := now();
    v_state   SMALLINT;
    v_attempt SMALLINT;
    v_version INT;
    v_next    TIMESTAMPTZ;
    v_rfid    SMALLINT;
    v_result  BYTEA;
    v_rcode   SMALLINT;
    v_rmsg    VARCHAR;
BEGIN

    SELECT a.state_code, a.attempt_number, a.version, a.next_retry_at_utc,
           a.result_format_id, a.result, a.reason_code, a.reason_message
      INTO v_state, v_attempt, v_version, v_next, v_rfid, v_result, v_rcode, v_rmsg
      FROM {{schema}}.steps a
     WHERE a.job_id = p_job_id AND a.name = p_name
     FOR UPDATE;

    IF NOT FOUND THEN
        INSERT INTO {{schema}}.steps (
            job_id, name, state_code, attempt_number,
            result_format_id, created_at_utc, modified_at_utc, version)
        VALUES (
            p_job_id, p_name, 10 /* JobStepStateCode.Pending */, 1,
            0 /* JobPayloadFormat.None */, v_now, v_now, 0)
        RETURNING steps.attempt_number, steps.version
             INTO v_attempt, v_version;

        RETURN QUERY SELECT 1 /* StartStepOutcomeCode.Invoke */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    ELSIF v_state = 100 /* JobStepStateCode.Succeeded */ THEN

        RETURN QUERY SELECT 3 /* StartStepOutcomeCode.ReplaySuccess */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, v_rfid, v_result, NULL::SMALLINT, NULL::VARCHAR;
    ELSIF v_state = 200 /* JobStepStateCode.Exhausted */ THEN

        RETURN QUERY SELECT 4 /* StartStepOutcomeCode.Exhausted */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, v_rcode, v_rmsg;
    ELSIF v_state = 230 /* JobStepStateCode.Interrupted */ THEN
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
           SET state_code      = 230 /* JobStepStateCode.Interrupted */,
               reason_code     = 63 /* JobEventReasonCode.JobStepInterrupted */,
               reason_message  = 'At-most-once step re-entered before completion; outcome unknown.',
               modified_at_utc = v_now,
               version         = a.version + 1
         WHERE a.job_id = p_job_id AND a.name = p_name
        RETURNING a.version INTO v_version;

        RETURN QUERY SELECT 5 /* StartStepOutcomeCode.Interrupted */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    ELSE
        UPDATE {{schema}}.steps a
           SET attempt_number  = a.attempt_number + 1,
               modified_at_utc = v_now,
               version         = a.version + 1
         WHERE a.job_id = p_job_id AND a.name = p_name
        RETURNING a.attempt_number, a.version INTO v_attempt, v_version;

        RETURN QUERY SELECT 1 /* StartStepOutcomeCode.Invoke */::SMALLINT, v_attempt, v_version,
            NULL::TIMESTAMPTZ, 0 /* JobPayloadFormat.None */::SMALLINT, NULL::BYTEA, NULL::SMALLINT, NULL::VARCHAR;
    END IF;
END;
$$;
