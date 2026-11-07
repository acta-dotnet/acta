-- One batch in the caller's transaction; only the final SELECT returns rows.
-- A name is inserted only when it is absent: an upsert would burn a namespaces.namespace_id on every deploy. Two deploys
-- that add one name meet on its unique index, and the second waits for the first and adds nothing.
INSERT INTO {{schema}}.namespaces (namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version)
SELECT d.namespace_name, @p_namespace_status_code, now(), now(), 0
FROM json_array_elements_text(@p_namespace_names::json) AS d (namespace_name)
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces n
    WHERE n.namespace_name = d.namespace_name
)
ORDER BY d.namespace_name
ON CONFLICT (namespace_name) DO NOTHING;

-- The sys schedule lock shared, then every namespace's exclusive in id order, before any row lock another statement can
-- reach (docs/sql-internals.md, "Schedule lock"): two deploys of one namespace serialize, and a publication of a shared
-- calendar runs wholly before or after a deploy that binds it.
DROP TABLE IF EXISTS pg_temp._deploy_lock;
CREATE TEMP TABLE _deploy_lock ON COMMIT DROP AS
SELECT 1 AS granted
FROM (SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), 1 /* NameSyntax.SystemNamespaceId */)) s
UNION ALL
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), x.namespace_id)
    FROM (
        SELECT n.namespace_id
        FROM {{schema}}.namespaces n
        WHERE n.namespace_name IN (SELECT json_array_elements_text(@p_namespace_names::json))
        ORDER BY n.namespace_id
    ) x
) l;

SELECT
    n.namespace_name,
    n.namespace_id,
    n.deployment_version AS current_deployment_version,
    EXISTS (
        SELECT 1 FROM {{schema}}.deployments d
        WHERE d.namespace_id = n.namespace_id AND d.deployment_version = @p_deployment_version
    ) AS is_deployed
FROM {{schema}}.namespaces n
WHERE n.namespace_name IN (SELECT json_array_elements_text(@p_namespace_names::json));
