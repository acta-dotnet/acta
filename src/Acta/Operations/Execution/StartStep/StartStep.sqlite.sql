-- One UPDATE for a due pending slot re-entered on replay, keyed on @p_at_most_once:
--   at-least-once -> bump the attempt and re-run the body (state stays pending);
--   at-most-once  -> terminalize Interrupted (one transition/version bump) rather than re-invoke (an already-interrupted row, state 230, is not matched).
UPDATE {{schema}}.steps
SET
    step_status_code = CASE WHEN @p_at_most_once THEN 230 /* StepStatusCode.Interrupted */ ELSE step_status_code END,
    attempt_number = attempt_number + CASE WHEN @p_at_most_once THEN 0 ELSE 1 END,
    next_retry_at_utc = CASE WHEN @p_at_most_once THEN NULL ELSE next_retry_at_utc END,
    failure_code = CASE WHEN @p_at_most_once THEN 20 /* StepFailureCode.Interrupted */ ELSE failure_code END,
    failure_message
    = CASE WHEN @p_at_most_once THEN 'At-most-once step re-entered before completion; outcome unknown.' ELSE failure_message END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND step_name = @p_step_name
    AND step_status_code = 10 /* StepStatusCode.Pending */
    AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= {{now}});

INSERT INTO {{schema}}.steps (
    job_id, step_name, step_status_code, attempt_number,
    result_format_id, created_at_utc, modified_at_utc, version
)
SELECT
    @p_job_id,
    @p_step_name,
    10 /* StepStatusCode.Pending */,
    1,
    0 /* JobPayloadFormat.None */,
    {{now}},
    {{now}},
    0
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.steps a
    WHERE a.job_id = @p_job_id AND a.step_name = @p_step_name
);

SELECT
    CASE
        WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */ THEN 3 /* StartStepOutcomeCode.ReplaySuccess */
        WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN 4 /* StartStepOutcomeCode.Exhausted */
        WHEN a.step_status_code = 230 /* StepStatusCode.Interrupted */ THEN 5 /* StartStepOutcomeCode.Interrupted */
        WHEN a.next_retry_at_utc IS NOT NULL AND a.next_retry_at_utc > {{now}} THEN 2 /* StartStepOutcomeCode.Suspend */
        ELSE 1 /* StartStepOutcomeCode.Invoke */
    END AS outcome_code,
    a.attempt_number AS attempt_number,
    a.version AS version,
    CASE
        WHEN
            a.next_retry_at_utc IS NOT NULL AND a.next_retry_at_utc > {{now}}
            AND a.step_status_code = 10 /* StepStatusCode.Pending */
            THEN a.next_retry_at_utc
        ELSE NULL
    END AS next_retry_at_utc,
    CASE
        WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */
            THEN a.result_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS result_format_id,
    CASE WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */ THEN a.result ELSE NULL END AS result,
    CASE WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN a.failure_code ELSE NULL END AS failure_code,
    CASE WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN a.failure_message ELSE NULL END AS failure_message
FROM {{schema}}.steps a
WHERE a.job_id = @p_job_id AND a.step_name = @p_step_name;
