SELECT t.tenant_name FROM {{schema}}.tenants t WHERE t.tenant_id = @p_tenant_id;
