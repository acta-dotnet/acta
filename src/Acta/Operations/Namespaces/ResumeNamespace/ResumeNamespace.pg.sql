-- One statement: the name's row is locked, then resumed and audited only when it is not active yet.
WITH target AS (
    SELECT n.namespace_id, n.namespace_status_code, n.version
    FROM {{schema}}.namespaces n
    WHERE n.namespace_name = @p_namespace_name
    FOR UPDATE
),
resumed AS (
    UPDATE {{schema}}.namespaces AS n
    SET namespace_status_code = 10 /* NamespaceStatusCode.Active */, modified_at_utc = now(), version = n.version + 1
    FROM target t
    WHERE n.namespace_id = t.namespace_id AND t.namespace_status_code <> 10 /* NamespaceStatusCode.Active */
    RETURNING n.namespace_id, n.version
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        21 /* EventCode.NamespaceResumed */, now(), r.namespace_id,
        80 /* EntityCode.Namespace */, r.namespace_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM resumed r
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, r.version AS version
FROM resumed r
UNION ALL
SELECT 6 /* ControlAction.AlreadyInState */::SMALLINT AS action, t.version AS version
FROM target t
WHERE t.namespace_status_code = 10 /* NamespaceStatusCode.Active */
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
