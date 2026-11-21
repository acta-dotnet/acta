SELECT j.job_id
FROM {{schema}}.jobs j WITH (FORCESEEK (ux_jobs_idempotency_key_root (namespace_id, idempotency_key)))
INNER JOIN {{schema}}.namespaces ns WITH (FORCESEEK (ux_namespaces_name (namespace_name))) ON ns.namespace_id = j.namespace_id
WHERE
    ns.namespace_name = @p_namespace_name
    AND j.idempotency_key = @p_idempotency_key
    AND j.idempotency_key IS NOT NULL
    AND j.parent_job_id IS NULL;
