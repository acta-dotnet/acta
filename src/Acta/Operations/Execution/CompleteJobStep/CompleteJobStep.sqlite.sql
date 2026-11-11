-- The step changes only while this attempt holds the job's row (docs/sql-internals.md, "Execution fence").
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:complete_job_step: the job is no longer executing this attempt.')
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id = @p_leased_by_worker_id
);

-- Only an at-most-once body records a failure, and the failure ends the step Failed.
UPDATE {{schema}}.steps
SET
    step_status_code = CASE WHEN @p_succeeded THEN 100 /* StepStatusCode.Succeeded */ ELSE 200 /* StepStatusCode.Failed */ END,
    result_format_id = @p_result_format_id,
    result = @p_result,
    failure_message = @p_failure_message,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id = @p_job_id AND step_name = @p_step_name AND version = @p_version;

SELECT
    CASE
        WHEN CHANGES() = 0 THEN 4 /* CompleteJobStepOutcomeCode.StaleVersion */
        WHEN @p_succeeded THEN 1 /* CompleteJobStepOutcomeCode.Succeeded */
        ELSE 3 /* CompleteJobStepOutcomeCode.Failed */
    END AS outcome_code;
