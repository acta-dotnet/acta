CREATE OR REPLACE FUNCTION {{schema}}.update_namespace_metadata(
    p_namespace_name   VARCHAR,
    p_owner_team       VARCHAR,
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
    v_id SMALLINT; v_version INT;
BEGIN
    SELECT n.id, n.version INTO v_id, v_version
      FROM {{schema}}.namespaces n WHERE n.name = p_namespace_name FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* AdminControlAction.NotFound */::SMALLINT, NULL::INT; RETURN;
    END IF;

    IF v_version <> p_expected_version THEN
        RETURN QUERY SELECT 4 /* AdminControlAction.VersionConflict */::SMALLINT, v_version; RETURN;
    END IF;

    UPDATE {{schema}}.namespaces AS n
       SET owner_team = p_owner_team, description = p_description, modified_at_utc = now(), version = n.version + 1
     WHERE n.id = v_id
    RETURNING n.version INTO v_version;

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor_key,
        job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
        from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message)
    VALUES (
        22 /* JobEventCode.NamespaceMetadataChanged */, now(), v_id, p_actor_code, p_actor_key,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, p_reason_message);

    RETURN QUERY SELECT 1 /* AdminControlAction.Applied */::SMALLINT, v_version;
END;
$$;
