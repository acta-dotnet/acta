DROP TABLE IF EXISTS temp._update_namespace;

CREATE TEMP TABLE _update_namespace AS
SELECT n.namespace_id, n.version AS from_version
FROM {{schema}}.namespaces n
WHERE n.namespace_name = @p_namespace_name;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
SELECT
    22 /* EventCode.NamespaceUpdated */,
    {{now}},
    s.namespace_id,
    80 /* EntityCode.Namespace */,
    s.namespace_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._update_namespace s
WHERE s.from_version = @p_expected_version;

UPDATE {{schema}}.namespaces
SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = {{now}}, version = version + 1
WHERE
    namespace_name = @p_namespace_name
    AND version = @p_expected_version;

SELECT
    CASE
        WHEN s.namespace_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.namespace_id IS NULL THEN NULL
        WHEN s.from_version <> @p_expected_version THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._update_namespace s ON 1 = 1;
