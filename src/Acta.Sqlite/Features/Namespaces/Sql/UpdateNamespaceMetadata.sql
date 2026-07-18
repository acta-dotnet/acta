DROP TABLE IF EXISTS temp._update_namespace_metadata;

CREATE TEMP TABLE _update_namespace_metadata AS
SELECT n.id, n.version AS from_version
FROM {{schema}}.namespaces n
WHERE n.name = @p_namespace_name;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key,
    job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
    from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message)
SELECT
    22 /* JobEventCode.NamespaceMetadataChanged */, {{now}}, s.id, @p_actor_code, @p_actor_key,
    NULL, NULL, NULL, NULL, NULL, NULL, NULL,
    NULL, NULL, NULL, NULL, NULL, @p_reason_message
FROM temp._update_namespace_metadata s
WHERE s.from_version = @p_expected_version;

UPDATE {{schema}}.namespaces
   SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = {{now}}, version = version + 1
 WHERE name = @p_namespace_name
   AND version = @p_expected_version;

SELECT
    CASE WHEN s.id IS NULL THEN 2 /* AdminControlAction.NotFound */
         WHEN s.from_version <> @p_expected_version THEN 4 /* AdminControlAction.VersionConflict */
         ELSE 1 /* AdminControlAction.Applied */ END AS action,
    CASE WHEN s.id IS NULL THEN NULL
         WHEN s.from_version <> @p_expected_version THEN s.from_version
         ELSE s.from_version + 1 END AS version
FROM (SELECT 1) one
LEFT JOIN temp._update_namespace_metadata s ON 1 = 1;
