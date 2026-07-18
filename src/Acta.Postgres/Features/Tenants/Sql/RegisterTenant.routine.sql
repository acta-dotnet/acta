CREATE OR REPLACE FUNCTION {{schema}}.register_tenant(
    p_tenant_key   VARCHAR,
    p_display_name VARCHAR,
    p_description  VARCHAR,
    p_status_code  SMALLINT
)
RETURNS TABLE(tenant_id INT)
LANGUAGE sql
AS $$
    WITH upsert AS (
        INSERT INTO {{schema}}.tenants
            (tenant_key, display_name, description, status_code, created_at_utc, modified_at_utc, version)
        VALUES (p_tenant_key, p_display_name, p_description, p_status_code, now(), now(), 0)
        ON CONFLICT (tenant_key) DO UPDATE SET
            display_name    = EXCLUDED.display_name,
            description     = EXCLUDED.description,
            status_code     = EXCLUDED.status_code,
            modified_at_utc = now(),
            version         = {{schema}}.tenants.version + 1
          WHERE {{schema}}.tenants.status_code IS DISTINCT FROM EXCLUDED.status_code
             OR {{schema}}.tenants.display_name IS DISTINCT FROM EXCLUDED.display_name
             OR {{schema}}.tenants.description IS DISTINCT FROM EXCLUDED.description
        RETURNING id
    )
    SELECT id FROM upsert
    UNION ALL
    SELECT id FROM {{schema}}.tenants
     WHERE tenant_key = p_tenant_key AND NOT EXISTS (SELECT 1 FROM upsert);
$$;
