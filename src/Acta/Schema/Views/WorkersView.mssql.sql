SELECT
    w.id AS worker_id,
    w.worker_ref,
    ns.name AS namespace,
    {{decode:worker-status:w.status_code}} AS status,
    w.status_code,
    w.deployment_version,
    w.host,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrency,
    w.last_seen_at_utc,
    w.created_at_utc,
    w.modified_at_utc,
    w.version
FROM {{schema}}.workers AS w
JOIN {{schema}}.namespaces AS ns ON ns.id = w.namespace_id
