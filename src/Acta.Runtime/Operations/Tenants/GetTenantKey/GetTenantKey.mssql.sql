SELECT t.tenant_key FROM {{schema}}.tenants t WHERE t.id = @p_tenant_id;
