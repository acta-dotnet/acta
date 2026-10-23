SELECT
    input_format_id,
    input
FROM {{schema}}.jobs
WHERE job_id = @p_job_id;
