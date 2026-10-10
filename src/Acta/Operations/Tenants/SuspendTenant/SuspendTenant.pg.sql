-- One statement: the tenant's row is locked, then suspended and audited only when it is not suspended yet. The audit row
-- belongs to the seeded sys namespace (id 1, M001) and names the tenant.
WITH target AS (
    SELECT t.id, t.status_code, t.version
    FROM {{schema}}.tenants t
    WHERE t.tenant_name = @p_tenant_name
    FOR UPDATE
),
changed AS (
    UPDATE {{schema}}.tenants AS t
    SET status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = now(), version = t.version + 1
    FROM target x
    WHERE t.id = x.id AND x.status_code <> 20 /* TenantStatusCode.Suspended */
    RETURNING t.id, t.version
),
audit AS (
    INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor, tenant_id, reason_message)
    SELECT 10 /* EventCode.TenantSuspended */, now(), 1, @p_actor_code, @p_actor, c.id, @p_reason_message
    FROM changed c
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, c.version AS version
FROM changed c
UNION ALL
SELECT 6 /* ControlAction.AlreadyInState */::SMALLINT AS action, x.version AS version
FROM target x
WHERE x.status_code = 20 /* TenantStatusCode.Suspended */
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
