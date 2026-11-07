SELECT
    ns.namespace_name,
    d.deployment_version,
    d.created_at_utc
FROM {{schema}}.deployments AS d
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = d.namespace_id
