-- The step changes only while this attempt holds the job's row (docs/sql-internals.md, "Execution fence").
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:start_job_step: the job is no longer executing this attempt.')
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id = @p_leased_by_worker_id
);

-- A pending step re-entered on replay under AtMostOnce terminalizes Interrupted (one transition, one version bump)
-- rather than re-invoking; an ordinary one runs again, its row unchanged.
UPDATE {{schema}}.steps
SET
    step_status_code = 230 /* StepStatusCode.Interrupted */,
    failure_message = 'At-most-once step re-entered before completion; outcome unknown.',
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    @p_at_most_once
    AND job_id = @p_job_id AND step_name = @p_step_name
    AND step_status_code = 10 /* StepStatusCode.Pending */;

INSERT INTO {{schema}}.steps (
    job_id, step_name, step_status_code,
    result_format_id, created_at_utc, modified_at_utc, version
)
SELECT
    @p_job_id,
    @p_step_name,
    10 /* StepStatusCode.Pending */,
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
        WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */ THEN 3 /* StartJobStepOutcomeCode.ReplaySuccess */
        WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN 4 /* StartJobStepOutcomeCode.Exhausted */
        WHEN a.step_status_code = 230 /* StepStatusCode.Interrupted */ THEN 5 /* StartJobStepOutcomeCode.Interrupted */
        ELSE 1 /* StartJobStepOutcomeCode.Invoke */
    END AS outcome_code,
    a.version AS version,
    CASE
        WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */
            THEN a.result_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS result_format_id,
    CASE WHEN a.step_status_code = 100 /* StepStatusCode.Succeeded */ THEN a.result ELSE NULL END AS result,
    CASE WHEN a.step_status_code = 200 /* StepStatusCode.Exhausted */ THEN a.failure_message ELSE NULL END AS failure_message
FROM {{schema}}.steps a
WHERE a.job_id = @p_job_id AND a.step_name = @p_step_name;
