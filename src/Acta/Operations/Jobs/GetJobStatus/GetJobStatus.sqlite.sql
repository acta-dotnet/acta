SELECT r.job_status_code
FROM {{schema}}.jobs_state r
WHERE r.job_id = @p_job_id;
