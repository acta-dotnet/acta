SELECT t.tenant_id, t.tenant_name, t.display_name, t.description, t.tenant_status_code AS status, t.created_at_utc, t.modified_at_utc, t.version
FROM {{schema}}.tenants t
WHERE t.tenant_name = @p_tenant_name;
