SELECT
    w.id,
    ns.name,
    w.status_code,
    w.host,
    w.deployment_version,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrency,
    w.last_seen_at_utc,
    w.created_at_utc,
    w.modified_at_utc,
    w.worker_ref
FROM {{schema}}.workers w
JOIN {{schema}}.namespaces ns ON ns.id = w.namespace_id
WHERE w.worker_ref = @p_worker_ref;
