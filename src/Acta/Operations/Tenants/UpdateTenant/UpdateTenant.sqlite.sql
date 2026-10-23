DROP TABLE IF EXISTS temp._update_tenant;

CREATE TEMP TABLE _update_tenant AS
SELECT t.tenant_id, t.version AS from_version
FROM {{schema}}.tenants t
WHERE t.tenant_name = @p_tenant_name;

-- namespace_id 1 is the sys namespace the baseline seeds.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
SELECT
    12 /* EventCode.TenantUpdated */,
    {{now}},
    1,
    120 /* EntityCode.Tenant */,
    s.tenant_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._update_tenant s
WHERE s.from_version = @p_expected_version;

UPDATE {{schema}}.tenants
SET display_name = @p_display_name, description = @p_description, modified_at_utc = {{now}}, version = version + 1
WHERE
    tenant_name = @p_tenant_name
    AND version = @p_expected_version;

SELECT
    CASE
        WHEN s.tenant_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.tenant_id IS NULL THEN NULL
        WHEN s.from_version <> @p_expected_version THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._update_tenant s ON 1 = 1;
