DROP TABLE IF EXISTS temp._update_tenant;

CREATE TEMP TABLE _update_tenant AS
SELECT t.id, t.version AS from_version
FROM {{schema}}.tenants t
WHERE t.tenant_name = @p_tenant_name;

-- namespace_id 1 is the seeded sys namespace (M001).
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    12 /* EventCode.TenantUpdated */,
    {{now}},
    1,
    @p_actor_code,
    @p_actor,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    s.id,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    @p_reason_message
FROM temp._update_tenant s
WHERE s.from_version = @p_expected_version;

UPDATE {{schema}}.tenants
SET display_name = @p_display_name, description = @p_description, modified_at_utc = {{now}}, version = version + 1
WHERE
    tenant_name = @p_tenant_name
    AND version = @p_expected_version;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_version <> @p_expected_version THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._update_tenant s ON 1 = 1;
