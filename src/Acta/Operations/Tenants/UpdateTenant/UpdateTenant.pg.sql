-- One statement: the tenant's row is locked, then updated and audited only at the expected version. The audit
-- row belongs to the sys namespace the baseline seeds (id 1) and names the tenant.
WITH target AS (
    SELECT t.tenant_id, t.version
    FROM {{schema}}.tenants t
    WHERE t.tenant_name = @p_tenant_name
    FOR UPDATE
),
updated AS (
    UPDATE {{schema}}.tenants AS t
    SET display_name = @p_display_name, description = @p_description, modified_at_utc = now(), version = t.version + 1
    FROM target x
    WHERE t.tenant_id = x.tenant_id AND x.version = @p_expected_version
    RETURNING t.tenant_id, t.version
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        12 /* EventCode.TenantUpdated */, now(), 1,
        120 /* EntityCode.Tenant */, u.tenant_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM updated u
)
SELECT 1 /* ControlAction.Applied */::SMALLINT AS action, u.version AS version
FROM updated u
UNION ALL
SELECT 5 /* ControlAction.VersionConflict */::SMALLINT AS action, x.version AS version
FROM target x
WHERE x.version <> @p_expected_version
UNION ALL
SELECT 2 /* ControlAction.NotFound */::SMALLINT AS action, NULL::INT AS version
WHERE NOT EXISTS (SELECT 1 FROM target);
