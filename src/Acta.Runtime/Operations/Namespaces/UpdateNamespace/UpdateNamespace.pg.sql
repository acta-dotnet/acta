-- One statement: the name's row is locked, then updated and audited only at the expected version.
WITH target AS (
    SELECT n.id, n.version
    FROM {{schema}}.namespaces n
    WHERE n.name = @p_namespace_name
    FOR UPDATE
),
updated AS (
    UPDATE {{schema}}.namespaces AS n
    SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = now(), version = n.version + 1
    FROM target t
    WHERE n.id = t.id AND t.version = @p_expected_version
    RETURNING n.id, n.version
),
audit AS (
    INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor_key, reason_message)
    SELECT 22 /* EventCode.NamespaceUpdated */, now(), u.id, @p_actor_code, @p_actor_key, @p_reason_message
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
