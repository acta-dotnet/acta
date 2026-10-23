-- One statement: the name's row is locked, then updated and audited only at the expected version.
WITH target AS (
    SELECT n.namespace_id, n.version
    FROM {{schema}}.namespaces n
    WHERE n.namespace_name = @p_namespace_name
    FOR UPDATE
),
updated AS (
    UPDATE {{schema}}.namespaces AS n
    SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = now(), version = n.version + 1
    FROM target t
    WHERE n.namespace_id = t.namespace_id AND t.version = @p_expected_version
    RETURNING n.namespace_id, n.version
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        22 /* EventCode.NamespaceUpdated */, now(), u.namespace_id,
        80 /* EntityCode.Namespace */, u.namespace_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM updated u
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, u.version AS version
FROM updated u
UNION ALL
SELECT 5 /* ControlAction.VersionConflict */::SMALLINT AS action, t.version AS version
FROM target t
WHERE t.version <> @p_expected_version
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
