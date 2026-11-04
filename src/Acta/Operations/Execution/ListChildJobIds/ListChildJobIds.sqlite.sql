SELECT j.job_id
FROM {{schema}}.jobs j
WHERE j.parent_job_id = @p_parent_job_id;
