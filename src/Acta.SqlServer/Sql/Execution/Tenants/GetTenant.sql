SELECT
    t.id,
    t.tenant_key,
    t.display_name,
    t.description,
    t.status_code,
    t.created_at_utc,
    t.modified_at_utc,
    t.version
FROM {{schema}}.tenants t
WHERE
    (@p_tenant_key IS NOT NULL AND t.tenant_key = @p_tenant_key)
    OR (@p_tenant_id IS NOT NULL AND t.id = @p_tenant_id);
