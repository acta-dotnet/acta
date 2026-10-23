INSERT INTO {{schema}}.tenants (tenant_name, display_name, description, tenant_status_code)
VALUES (@p_tenant_name, @p_display_name, @p_description, 10 /* TenantStatusCode.Active */)
ON CONFLICT (tenant_name) DO NOTHING;

SELECT tenant_id, tenant_name FROM {{schema}}.tenants
WHERE tenant_name = @p_tenant_name;
