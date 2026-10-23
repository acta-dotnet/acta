UPDATE {{schema}}.steps
SET
    step_status_code = CASE
        WHEN @p_succeeded THEN 100 /* StepStatusCode.Succeeded */
        WHEN
            (attempt_number >= @p_max_attempts)
            OR (
                @p_retry_window_seconds IS NOT NULL
                AND {{now}} + (@p_delay_seconds) * 1000
                > created_at_utc + (@p_retry_window_seconds) * 1000
            )
            THEN 200 /* StepStatusCode.Exhausted */
        ELSE step_status_code
    END,
    result_format_id = CASE WHEN @p_succeeded THEN @p_result_format_id ELSE result_format_id END,
    result = CASE WHEN @p_succeeded THEN @p_result ELSE result END,
    next_retry_at_utc = CASE
        WHEN @p_succeeded THEN NULL
        WHEN
            (attempt_number >= @p_max_attempts)
            OR (
                @p_retry_window_seconds IS NOT NULL
                AND {{now}} + (@p_delay_seconds) * 1000
                > created_at_utc + (@p_retry_window_seconds) * 1000
            )
            THEN NULL
        ELSE {{now}} + (@p_delay_seconds) * 1000
    END,
    -- A success supersedes any earlier failed attempt, so the row must not keep that attempt's failure
    -- snapshot: a Succeeded row with a failure_code reads as a failure to every post-mortem query.
    failure_code = CASE WHEN @p_succeeded THEN NULL ELSE @p_failure_code END,
    failure_message = CASE WHEN @p_succeeded THEN NULL ELSE @p_failure_message END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

SELECT
    CASE
        WHEN CHANGES() = 0 THEN 4 /* CompleteStepOutcomeCode.StaleVersion */
        WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */ THEN 1 /* CompleteStepOutcomeCode.Succeeded */
        WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN 3 /* CompleteStepOutcomeCode.Exhausted */
        ELSE 2 /* CompleteStepOutcomeCode.RetryScheduled */
    END AS outcome_code,
    CASE
        WHEN CHANGES() > 0 AND a.step_status_code = 10 /* StepStatusCode.Pending */
            THEN a.next_retry_at_utc
        ELSE NULL
    END AS next_retry_at_utc
FROM (SELECT 1) one
LEFT JOIN {{schema}}.steps a
    ON a.job_id = @p_job_id AND a.step_name = @p_step_name;
