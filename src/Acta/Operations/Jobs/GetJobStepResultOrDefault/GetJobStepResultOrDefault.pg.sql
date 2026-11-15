-- One step of the job by its canonical name, sought on the key; empty unless it succeeded with a result.
SELECT s.result_format_id, s.result
FROM {{schema}}.steps s
WHERE
    s.job_id = @p_job_id
    AND s.step_name = @p_step_name
    AND s.step_status_code = 100 /* StepStatusCode.Succeeded */
    AND s.result IS NOT NULL;
