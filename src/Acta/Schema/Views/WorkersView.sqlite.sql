SELECT
    w.worker_id,
    w.worker_ref,
    ns.namespace_name AS namespace,
    {{decode:worker-status:w.worker_status_code}} AS status,
    w.worker_status_code,
    w.deployment_version,
    w.host_name,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrent_executors,
    w.last_seen_at_utc,
    w.created_at_utc,
    w.modified_at_utc,
    w.version
FROM {{schema}}.workers AS w
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = w.namespace_id
