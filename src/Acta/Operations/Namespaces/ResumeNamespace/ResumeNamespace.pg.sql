-- One statement: the name's row is locked, then resumed and audited only when it is not active yet.
WITH target AS (
    SELECT n.id, n.status_code, n.version
    FROM {{schema}}.namespaces n
    WHERE n.name = @p_namespace_name
    FOR UPDATE
),
resumed AS (
    UPDATE {{schema}}.namespaces AS n
    SET status_code = 10 /* NamespaceStatusCode.Active */, modified_at_utc = now(), version = n.version + 1
    FROM target t
    WHERE n.id = t.id AND t.status_code <> 10 /* NamespaceStatusCode.Active */
    RETURNING n.id, n.version
),
audit AS (
    INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor, reason_message)
    SELECT 21 /* EventCode.NamespaceResumed */, now(), r.id, @p_actor_code, @p_actor, @p_reason_message
    FROM resumed r
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, r.version AS version
FROM resumed r
UNION ALL
SELECT 6 /* ControlAction.AlreadyInState */::SMALLINT AS action, t.version AS version
FROM target t
WHERE t.status_code = 10 /* NamespaceStatusCode.Active */
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
