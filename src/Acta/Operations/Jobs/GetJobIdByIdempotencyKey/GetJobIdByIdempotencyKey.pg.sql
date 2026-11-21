SELECT j.job_id
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
WHERE
    ns.namespace_name = @p_namespace_name
    AND j.idempotency_key = @p_idempotency_key
    AND j.parent_job_id IS NULL;
