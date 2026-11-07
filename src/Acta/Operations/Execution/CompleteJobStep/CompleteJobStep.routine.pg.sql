CREATE OR REPLACE FUNCTION {{schema}}.complete_job_step(
    p_job_id BIGINT,
    p_execution_number INT,
    p_leased_by_worker_id INT,
    p_step_name VARCHAR,
    p_succeeded BOOLEAN,
    p_result_format_id SMALLINT,
    p_result BYTEA,
    p_failure_code SMALLINT,
    p_failure_message VARCHAR,
    p_version INT
)
RETURNS TABLE (
    outcome_code SMALLINT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_rows INT;
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
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:complete_job_step: job % is no longer executing attempt %.', p_job_id, p_execution_number
            USING ERRCODE = 'P0001';
    END IF;

    -- Only an at-most-once body records a failure, and the failure ends the step Exhausted.
    UPDATE {{schema}}.steps a
    SET
        step_status_code = CASE WHEN p_succeeded THEN 100 /* StepStatusCode.Succeeded */ ELSE 200 /* StepStatusCode.Exhausted */ END,
        result_format_id = p_result_format_id,
        result = p_result,
        failure_code = p_failure_code,
        failure_message = p_failure_message,
        modified_at_utc = v_now,
        version = a.version + 1
    WHERE a.job_id = p_job_id AND a.step_name = p_step_name AND a.version = p_version;
    GET DIAGNOSTICS v_rows = ROW_COUNT;

    RETURN QUERY SELECT CASE
        WHEN v_rows = 0 THEN 4 /* CompleteJobStepOutcomeCode.StaleVersion */
        WHEN p_succeeded THEN 1 /* CompleteJobStepOutcomeCode.Succeeded */
        ELSE 3 /* CompleteJobStepOutcomeCode.Exhausted */
    END::SMALLINT;
END;
$$;
