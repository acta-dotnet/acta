SELECT
    ns.namespace_id,
    ns.namespace_name AS job_namespace,
    ns.namespace_status_code AS status,
    ns.owner_team,
    ns.description,
    ns.deployment_version,
    ns.version
FROM {{schema}}.namespaces ns
WHERE ns.namespace_name = @p_namespace_name;
