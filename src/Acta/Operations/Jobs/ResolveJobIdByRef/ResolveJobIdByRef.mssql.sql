SELECT j.id
FROM {{schema}}.jobs j
WHERE j.job_ref = @p_job_ref;
