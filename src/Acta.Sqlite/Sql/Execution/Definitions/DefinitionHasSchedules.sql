SELECT 1
FROM {{schema}}.schedules s
INNER JOIN {{schema}}.jobs j ON j.id = s.job_id
WHERE j.definition_id = @p_definition_id;
