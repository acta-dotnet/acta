SELECT j.job_id
FROM {{schema}}.jobs j WITH (FORCESEEK (ux_jobs_deduplication_key_root (namespace_id, deduplication_key)))
INNER JOIN {{schema}}.namespaces ns WITH (FORCESEEK (ux_namespaces_name (namespace_name))) ON ns.namespace_id = j.namespace_id
WHERE
    ns.namespace_name = @p_namespace_name
    AND j.deduplication_key = @p_deduplication_key
    AND j.deduplication_key IS NOT NULL
    AND j.parent_job_id IS NULL;
