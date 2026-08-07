UPDATE {{schema}}.steps
SET
    status_code = CASE
        WHEN @p_succeeded THEN 100 /* JobStepStatusCode.Succeeded */
        WHEN
            (attempt_number >= @p_max_attempts)
            OR (
                @p_retry_window_seconds IS NOT NULL
                AND {{now}} + (@p_delay_seconds) * 1000
                > created_at_utc + (@p_retry_window_seconds) * 1000
            )
            THEN 200 /* JobStepStatusCode.Exhausted */
        ELSE status_code
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
    reason_code = CASE WHEN @p_succeeded THEN reason_code ELSE @p_reason_code END,
    reason_message = CASE WHEN @p_succeeded THEN reason_message ELSE @p_reason_message END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id = @p_job_id AND name = @p_name AND version = @p_version;

SELECT
    CASE
        WHEN CHANGES() = 0 THEN 4 /* CompleteStepOutcomeCode.StaleVersion */
        WHEN a.status_code = 100 /* JobStepStatusCode.Succeeded */ THEN 1 /* CompleteStepOutcomeCode.Succeeded */
        WHEN a.status_code = 200 /* JobStepStatusCode.Exhausted */ THEN 3 /* CompleteStepOutcomeCode.Exhausted */
        ELSE 2 /* CompleteStepOutcomeCode.RetryScheduled */
    END AS outcome_code,
    CASE
        WHEN CHANGES() > 0 AND a.status_code = 10 /* JobStepStatusCode.Pending */
            THEN a.next_retry_at_utc
        ELSE NULL
    END AS next_retry_at_utc
FROM (SELECT 1) one
LEFT JOIN {{schema}}.steps a
    ON a.job_id = @p_job_id AND a.name = @p_name;
