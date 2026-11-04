SELECT j.job_id
FROM {{schema}}.namespaces n
INNER JOIN {{schema}}.definitions d ON d.namespace_id = n.namespace_id AND d.definition_name = @p_job_name
INNER JOIN {{schema}}.jobs j ON j.definition_id = d.definition_id AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
WHERE n.namespace_name = @p_namespace_name;
