-- One statement: the name's row is locked, then suspended and audited only when it is not suspended yet.
WITH target AS (
    SELECT n.id, n.status_code, n.version
    FROM {{schema}}.namespaces n
    WHERE n.name = @p_namespace_name
    FOR UPDATE
),
suspended AS (
    UPDATE {{schema}}.namespaces AS n
    SET status_code = 20 /* NamespaceStatusCode.Suspended */, modified_at_utc = now(), version = n.version + 1
    FROM target t
    WHERE n.id = t.id AND t.status_code <> 20 /* NamespaceStatusCode.Suspended */
    RETURNING n.id, n.version
),
audit AS (
    INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor, reason_message)
    SELECT 20 /* EventCode.NamespaceSuspended */, now(), s.id, @p_actor_code, @p_actor, @p_reason_message
    FROM suspended s
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, s.version AS version
FROM suspended s
UNION ALL
SELECT 6 /* ControlAction.AlreadyInState */::SMALLINT AS action, t.version AS version
FROM target t
WHERE t.status_code = 20 /* NamespaceStatusCode.Suspended */
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
