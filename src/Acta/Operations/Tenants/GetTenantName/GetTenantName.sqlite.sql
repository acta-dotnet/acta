SELECT t.tenant_name FROM {{schema}}.tenants t WHERE t.id = @p_tenant_id;
