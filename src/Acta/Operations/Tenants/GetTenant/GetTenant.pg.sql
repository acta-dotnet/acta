SELECT t.id AS tenant_id, t.tenant_key, t.display_name, t.description, t.status_code AS status, t.created_at_utc, t.modified_at_utc, t.version
FROM {{schema}}.tenants t
WHERE t.tenant_key = @p_tenant_key;
