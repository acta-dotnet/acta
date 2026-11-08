SELECT
    w.worker_id,
    w.worker_ref,
    ns.namespace_name,
    {{decode:worker-status:w.worker_status_code}} AS worker_status,
    w.worker_status_code,
    w.deployment_version,
    w.host_name,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrent_executors,
    strftime('%Y-%m-%dT%H:%M:%fZ', w.last_seen_at_utc / 1000.0, 'unixepoch') AS last_seen_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', w.created_at_utc / 1000.0, 'unixepoch') AS created_at_utc,
    strftime('%Y-%m-%dT%H:%M:%fZ', w.modified_at_utc / 1000.0, 'unixepoch') AS modified_at_utc,
    w.version
FROM {{schema}}.workers AS w
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = w.namespace_id
