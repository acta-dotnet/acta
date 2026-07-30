CREATE OR REPLACE FUNCTION {{schema}}.resume_namespace(
    p_namespace_name VARCHAR,
    p_actor_code     SMALLINT,
    p_actor_key       VARCHAR,
    p_reason_message VARCHAR
)
RETURNS TABLE(action SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_id      SMALLINT;
    v_status  SMALLINT;
    v_version INT;
BEGIN
    SELECT n.id, n.status_code, n.version INTO v_id, v_status, v_version
      FROM {{schema}}.namespaces n WHERE n.name = p_namespace_name FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* AdminControlAction.NotFound */::SMALLINT, NULL::INT; RETURN;
    END IF;
    IF v_status = 10 /* JobNamespaceStatusCode.Active */ THEN
        RETURN QUERY SELECT 3 /* AdminControlAction.AlreadyInState */::SMALLINT, v_version; RETURN;
    END IF;
    UPDATE {{schema}}.namespaces AS n
       SET status_code = 10 /* JobNamespaceStatusCode.Active */, modified_at_utc = now(), version = n.version + 1
     WHERE n.id = v_id
    RETURNING n.version INTO v_version;
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor_key,
        job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
        from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message)
    VALUES (
        21 /* JobEventCode.NamespaceResumed */, now(), v_id, p_actor_code, p_actor_key,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, p_reason_message);
    RETURN QUERY SELECT 1 /* AdminControlAction.Applied */::SMALLINT, v_version;
END;
$$;
