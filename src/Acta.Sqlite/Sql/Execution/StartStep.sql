-- One UPDATE for a due pending slot re-entered on replay, keyed on @p_at_most_once:
--   at-least-once -> bump the attempt and re-run the body (state stays pending);
--   at-most-once  -> terminalize Interrupted (one transition/version bump) rather than re-invoke (an already-interrupted row, state 40, is not matched).
UPDATE {{schema}}.steps
SET
    status_code = CASE WHEN @p_at_most_once THEN 230 /* JobStepStatusCode.Interrupted */ ELSE status_code END,
    attempt_number = attempt_number + CASE WHEN @p_at_most_once THEN 0 ELSE 1 END,
    reason_code = CASE WHEN @p_at_most_once THEN 63 /* JobEventReasonCode.JobStepInterrupted */ ELSE reason_code END,
    reason_message
    = CASE WHEN @p_at_most_once THEN 'At-most-once step re-entered before completion; outcome unknown.' ELSE reason_message END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND name = @p_name
    AND status_code = 10 /* JobStepStatusCode.Pending */
    AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= {{now}});

INSERT INTO {{schema}}.steps (
    job_id, name, status_code, attempt_number,
    result_format_id, created_at_utc, modified_at_utc, version
)
SELECT
    @p_job_id,
    @p_name,
    10 /* JobStepStatusCode.Pending */,
    1,
    0 /* JobPayloadFormat.None */,
    {{now}},
    {{now}},
    0
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.steps a
    WHERE a.job_id = @p_job_id AND a.name = @p_name
);

SELECT
    CASE
        WHEN a.status_code = 100 /* JobStepStatusCode.Succeeded */ THEN 3 /* StartStepOutcomeCode.ReplaySuccess */
        WHEN a.status_code = 200 /* JobStepStatusCode.Exhausted */ THEN 4 /* StartStepOutcomeCode.Exhausted */
        WHEN a.status_code = 230 /* JobStepStatusCode.Interrupted */ THEN 5 /* StartStepOutcomeCode.Interrupted */
        WHEN a.next_retry_at_utc IS NOT NULL AND a.next_retry_at_utc > {{now}} THEN 2 /* StartStepOutcomeCode.Suspend */
        ELSE 1 /* StartStepOutcomeCode.Invoke */
    END AS outcome_code,
    a.attempt_number AS attempt_number,
    a.version AS version,
    CASE
        WHEN
            a.next_retry_at_utc IS NOT NULL AND a.next_retry_at_utc > {{now}}
            AND a.status_code = 10 /* JobStepStatusCode.Pending */
            THEN a.next_retry_at_utc
        ELSE NULL
    END AS next_retry_at_utc,
    CASE
        WHEN a.status_code = 100 /* JobStepStatusCode.Succeeded */
            THEN a.result_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS result_format_id,
    CASE WHEN a.status_code = 100 /* JobStepStatusCode.Succeeded */ THEN a.result ELSE NULL END AS result,
    CASE WHEN a.status_code = 200 /* JobStepStatusCode.Exhausted */ THEN a.reason_code ELSE NULL END AS reason_code,
    CASE WHEN a.status_code = 200 /* JobStepStatusCode.Exhausted */ THEN a.reason_message ELSE NULL END AS reason_message
FROM {{schema}}.steps a
WHERE a.job_id = @p_job_id AND a.name = @p_name;
