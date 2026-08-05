CREATE OR REPLACE FUNCTION {{schema}}.complete_step(
    p_job_id               BIGINT,
    p_name                 VARCHAR,
    p_succeeded            BOOLEAN,
    p_result_format_id     SMALLINT,
    p_result               BYTEA,
    p_reason_code          SMALLINT,
    p_reason_message       VARCHAR,
    p_delay_seconds        INT,
    p_max_attempts         SMALLINT,
    p_retry_window_seconds INT,
    p_version              INT
)
RETURNS TABLE (
    outcome_code      SMALLINT,
    next_retry_at_utc TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now     TIMESTAMPTZ := now();
    v_attempt SMALLINT;
    v_created TIMESTAMPTZ;
    v_next    TIMESTAMPTZ;
    v_exhaust BOOLEAN;
    v_rows    INT;
BEGIN

    SELECT a.attempt_number, a.created_at_utc INTO v_attempt, v_created
      FROM {{schema}}.steps a
     WHERE a.job_id = p_job_id AND a.name = p_name;

    IF p_succeeded THEN
        UPDATE {{schema}}.steps a
           SET status_code        = 100 /* JobStepStatusCode.Succeeded */,
               result_format_id  = p_result_format_id,
               result            = p_result,
               next_retry_at_utc = NULL,
               modified_at_utc   = v_now,
               version           = a.version + 1
         WHERE a.job_id = p_job_id AND a.name = p_name AND a.version = p_version;
        GET DIAGNOSTICS v_rows = ROW_COUNT;

        RETURN QUERY SELECT CASE WHEN v_rows = 0
                                 THEN 4 /* CompleteStepOutcomeCode.StaleVersion */
                                 ELSE 1 /* CompleteStepOutcomeCode.Succeeded */ END::SMALLINT, NULL::TIMESTAMPTZ;
        RETURN;
    END IF;

    v_next := v_now + make_interval(secs => p_delay_seconds);
    v_exhaust := (v_attempt >= p_max_attempts)
        OR (p_retry_window_seconds IS NOT NULL
            AND v_next > v_created + make_interval(secs => p_retry_window_seconds));

    IF v_exhaust THEN
        UPDATE {{schema}}.steps a
           SET status_code        = 200 /* JobStepStatusCode.Exhausted */,
               next_retry_at_utc = NULL,
               reason_code       = p_reason_code,
               reason_message    = p_reason_message,
               modified_at_utc   = v_now,
               version           = a.version + 1
         WHERE a.job_id = p_job_id AND a.name = p_name AND a.version = p_version;
        GET DIAGNOSTICS v_rows = ROW_COUNT;

        RETURN QUERY SELECT CASE WHEN v_rows = 0
                                 THEN 4 /* CompleteStepOutcomeCode.StaleVersion */
                                 ELSE 3 /* CompleteStepOutcomeCode.Exhausted */ END::SMALLINT, NULL::TIMESTAMPTZ;
    ELSE
        UPDATE {{schema}}.steps a
           SET next_retry_at_utc = v_next,
               reason_code       = p_reason_code,
               reason_message    = p_reason_message,
               modified_at_utc   = v_now,
               version           = a.version + 1
         WHERE a.job_id = p_job_id AND a.name = p_name AND a.version = p_version;
        GET DIAGNOSTICS v_rows = ROW_COUNT;

        RETURN QUERY SELECT CASE WHEN v_rows = 0
                                 THEN 4 /* CompleteStepOutcomeCode.StaleVersion */
                                 ELSE 2 /* CompleteStepOutcomeCode.RetryScheduled */ END::SMALLINT,
                            CASE WHEN v_rows = 0 THEN NULL ELSE v_next END;
    END IF;
END;
$$;
