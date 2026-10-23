DROP TABLE IF EXISTS temp._suspend_tenant;

CREATE TEMP TABLE _suspend_tenant AS
SELECT t.tenant_id, t.tenant_status_code AS from_status, t.version AS from_version
FROM {{schema}}.tenants t
WHERE t.tenant_name = @p_tenant_name;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
-- namespace_id 1 is the sys namespace the baseline seeds.
SELECT
    10 /* EventCode.TenantSuspended */,
    {{now}},
    1,
    120 /* EntityCode.Tenant */,
    s.tenant_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._suspend_tenant s
WHERE s.from_status <> 20 /* TenantStatusCode.Suspended */;

UPDATE {{schema}}.tenants
SET tenant_status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = {{now}}, version = version + 1
WHERE
    tenant_name = @p_tenant_name
    AND tenant_status_code <> 20 /* TenantStatusCode.Suspended */;

SELECT
    CASE
        WHEN s.tenant_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status = 20 /* TenantStatusCode.Suspended */ THEN 6 /* ControlAction.AlreadyInState */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.tenant_id IS NULL THEN NULL
        WHEN s.from_status = 20 /* TenantStatusCode.Suspended */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._suspend_tenant s ON 1 = 1;
