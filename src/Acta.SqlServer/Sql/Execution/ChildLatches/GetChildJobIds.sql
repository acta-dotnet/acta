SELECT j.id AS job_id
FROM {{schema}}.jobs j
WHERE j.parent_id = @p_parent_id;
