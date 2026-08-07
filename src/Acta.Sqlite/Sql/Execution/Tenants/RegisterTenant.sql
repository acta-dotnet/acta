INSERT INTO {{schema}}.tenants (tenant_key, display_name, description, status_code)
VALUES (@p_tenant_key, @p_display_name, @p_description, 10 /* TenantStatusCode.Active */)
ON CONFLICT (tenant_key) DO NOTHING;

SELECT id AS tenant_id FROM {{schema}}.tenants
WHERE tenant_key = @p_tenant_key;
