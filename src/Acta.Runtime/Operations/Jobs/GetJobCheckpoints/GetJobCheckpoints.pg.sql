SELECT kind_code, name, status_code, due_at_utc, value_format_id, value, created_at_utc, modified_at_utc
FROM {{schema}}.checkpoints
WHERE job_id = @p_job_id
ORDER BY kind_code, name;
