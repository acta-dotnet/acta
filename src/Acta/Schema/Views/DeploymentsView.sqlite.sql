SELECT
    ns.namespace_name,
    d.deployment_version,
    strftime('%Y-%m-%dT%H:%M:%fZ', d.created_at_utc / 1000.0, 'unixepoch') AS created_at_utc
FROM {{schema}}.deployments AS d
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = d.namespace_id
