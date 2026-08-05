CREATE OR REPLACE FUNCTION {{schema}}.update_tenant(
    p_tenant_key       VARCHAR,
    p_display_name     VARCHAR,
    p_description      VARCHAR,
    p_expected_version INT,
    p_actor_code       SMALLINT,
    p_actor_key         VARCHAR,
    p_reason_message   VARCHAR
)
RETURNS TABLE(action SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_id INT; v_version INT;
BEGIN
    SELECT t.id, t.version INTO v_id, v_version
      FROM {{schema}}.tenants t WHERE t.tenant_key = p_tenant_key FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* AdminControlAction.NotFound */::SMALLINT, NULL::INT; RETURN;
    END IF;

    IF v_version <> p_expected_version THEN
        RETURN QUERY SELECT 4 /* AdminControlAction.VersionConflict */::SMALLINT, v_version; RETURN;
    END IF;

    UPDATE {{schema}}.tenants AS t
       SET display_name = p_display_name, description = p_description, modified_at_utc = now(), version = t.version + 1
     WHERE t.id = v_id
    RETURNING t.version INTO v_version;

    -- namespace_id 1 is the seeded sys namespace (M001).
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor_key,
        job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
        from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message)
    VALUES (
        12 /* JobEventCode.TenantUpdated */, now(), 1, p_actor_code, p_actor_key,
        NULL, NULL, NULL, NULL, NULL, v_id, NULL,
        NULL, NULL, NULL, NULL, NULL, p_reason_message);

    RETURN QUERY SELECT 1 /* AdminControlAction.Applied */::SMALLINT, v_version;
END;
$$;
