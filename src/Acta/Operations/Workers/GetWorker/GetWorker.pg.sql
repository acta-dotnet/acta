SELECT
    w.worker_id,
    ns.namespace_name AS job_namespace,
    w.worker_status_code AS status,
    w.host_name,
    w.deployment_version,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrent_executors,
    w.last_seen_at_utc,
    w.created_at_utc AS started_at_utc,
    w.modified_at_utc,
    w.worker_ref
FROM {{schema}}.workers w
JOIN {{schema}}.namespaces ns ON ns.namespace_id = w.namespace_id
WHERE w.worker_ref = @p_worker_ref;
