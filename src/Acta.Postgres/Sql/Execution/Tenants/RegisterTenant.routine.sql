CREATE OR REPLACE FUNCTION {{schema}}.register_tenant(
    p_tenant_key VARCHAR,
    p_display_name VARCHAR,
    p_description VARCHAR
)
RETURNS TABLE (tenant_id INT)
LANGUAGE plpgsql
AS $$
BEGIN
    -- Two statements on purpose: each statement in a VOLATILE plpgsql body takes a fresh snapshot,
    -- so the SELECT sees the concurrently committed same-key winner that made ON CONFLICT skip (a
    -- single-statement sql body shares one snapshot across both arms and returns no row then).
    INSERT INTO {{schema}}.tenants
        (tenant_key, display_name, description, status_code, created_at_utc, modified_at_utc, version)
    VALUES (p_tenant_key, p_display_name, p_description, 10 /* TenantStatusCode.Active */, now(), now(), 0)
    ON CONFLICT (tenant_key) DO NOTHING;

    RETURN QUERY SELECT t.id FROM {{schema}}.tenants t WHERE t.tenant_key = p_tenant_key;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- four-parameter signature so pre-existing installs cannot resolve the stale upsert form.
DROP FUNCTION IF EXISTS {{schema}}.register_tenant(VARCHAR, VARCHAR, VARCHAR, SMALLINT);
