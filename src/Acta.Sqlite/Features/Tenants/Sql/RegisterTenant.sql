INSERT INTO {{schema}}.tenants (tenant_key, display_name, description, status_code)
VALUES (@p_tenant_key, @p_display_name, @p_description, @p_status_code)
ON CONFLICT (tenant_key) DO UPDATE SET
    display_name    = excluded.display_name,
    description     = excluded.description,
    status_code     = excluded.status_code,
    modified_at_utc = {{now}},
    version         = {{schema}}.tenants.version + 1
  WHERE {{schema}}.tenants.status_code IS NOT excluded.status_code
     OR {{schema}}.tenants.display_name IS NOT excluded.display_name
     OR {{schema}}.tenants.description IS NOT excluded.description;

SELECT id AS tenant_id FROM {{schema}}.tenants WHERE tenant_key = @p_tenant_key;
