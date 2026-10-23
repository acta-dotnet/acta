DROP TABLE IF EXISTS temp._resume_tenant;

CREATE TEMP TABLE _resume_tenant AS
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
    11 /* EventCode.TenantResumed */,
    {{now}},
    1,
    120 /* EntityCode.Tenant */,
    s.tenant_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._resume_tenant s
WHERE s.from_status <> 10 /* TenantStatusCode.Active */;

UPDATE {{schema}}.tenants
SET tenant_status_code = 10 /* TenantStatusCode.Active */, modified_at_utc = {{now}}, version = version + 1
WHERE
    tenant_name = @p_tenant_name
    AND tenant_status_code <> 10 /* TenantStatusCode.Active */;

SELECT
    CASE
        WHEN s.tenant_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status = 10 /* TenantStatusCode.Active */ THEN 6 /* ControlAction.AlreadyInState */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.tenant_id IS NULL THEN NULL
        WHEN s.from_status = 10 /* TenantStatusCode.Active */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._resume_tenant s ON 1 = 1;
