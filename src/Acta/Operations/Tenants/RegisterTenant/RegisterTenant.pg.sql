-- Two statements on purpose: each takes its own snapshot, so the SELECT sees a concurrently committed
-- same-key winner that made ON CONFLICT skip (one statement would share a snapshot and return no row).
INSERT INTO {{schema}}.tenants (tenant_name, display_name, description, status_code, created_at_utc, modified_at_utc, version)
VALUES (@p_tenant_name, @p_display_name, @p_description, 10 /* TenantStatusCode.Active */, now(), now(), 0)
ON CONFLICT (tenant_name) DO NOTHING;

SELECT t.id AS tenant_id, t.tenant_name
FROM {{schema}}.tenants t
WHERE t.tenant_name = @p_tenant_name;
