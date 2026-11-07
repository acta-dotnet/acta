/* A name is inserted only when it is absent: an upsert would advance sqlite_sequence on every deploy. The caller's
   transaction holds the database's one write lock, so two deploys serialize without a lock of their own. */
INSERT INTO {{schema}}.namespaces (namespace_name, namespace_status_code)
SELECT d.value, @p_namespace_status_code
FROM json_each(@p_namespace_names) d
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces n
    WHERE n.namespace_name = d.value
)
ORDER BY d.value;

SELECT
    n.namespace_name,
    n.namespace_id,
    n.deployment_version AS current_deployment_version,
    EXISTS (
        SELECT 1 FROM {{schema}}.deployments d
        WHERE d.namespace_id = n.namespace_id AND d.deployment_version = @p_deployment_version
    ) AS is_deployed
FROM {{schema}}.namespaces n
WHERE n.namespace_name IN (SELECT d.value FROM json_each(@p_namespace_names) d);
