DROP TABLE IF EXISTS temp._suspend_tenant;

CREATE TEMP TABLE _suspend_tenant AS
SELECT t.id, t.status_code AS from_status, t.version AS from_version
FROM {{schema}}.tenants t
WHERE t.tenant_key = @p_tenant_key;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
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
-- namespace_id 1 is the seeded sys namespace (M001).
SELECT
    10 /* EventCode.TenantSuspended */,
    {{now}},
    1,
    @p_actor_code,
    @p_actor_key,
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
FROM temp._suspend_tenant s
WHERE s.from_status <> 20 /* TenantStatusCode.Suspended */;

UPDATE {{schema}}.tenants
SET status_code = 20 /* TenantStatusCode.Suspended */, modified_at_utc = {{now}}, version = version + 1
WHERE
    tenant_key = @p_tenant_key
    AND status_code <> 20 /* TenantStatusCode.Suspended */;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* AdminControlAction.NotFound */
        WHEN s.from_status = 20 /* TenantStatusCode.Suspended */ THEN 3 /* AdminControlAction.AlreadyInState */
        ELSE 1 /* AdminControlAction.Applied */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_status = 20 /* TenantStatusCode.Suspended */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._suspend_tenant s ON 1 = 1;
