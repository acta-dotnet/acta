-- One statement: the tenant's row is locked, then suspended and audited only when it is not suspended yet. The audit row
-- belongs to the sys namespace the baseline seeds (id 1) and names the tenant.
WITH target AS (
    SELECT t.tenant_id, t.tenant_status_code, t.version
    FROM {{schema}}.tenants t
    WHERE t.tenant_name = @p_tenant_name
    FOR UPDATE
),
changed AS (
    UPDATE {{schema}}.tenants AS t
    SET tenant_status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = now(), version = t.version + 1
    FROM target x
    WHERE t.tenant_id = x.tenant_id AND x.tenant_status_code <> 20 /* TenantStatusCode.Suspended */
    RETURNING t.tenant_id, t.version
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        10 /* EventCode.TenantSuspended */, now(), 1,
        120 /* EntityCode.Tenant */, c.tenant_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM changed c
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, c.version AS version
FROM changed c
UNION ALL
SELECT 6 /* ControlAction.AlreadyInState */::SMALLINT AS action, x.version AS version
FROM target x
WHERE x.tenant_status_code = 20 /* TenantStatusCode.Suspended */
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
