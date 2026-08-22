CREATE OR REPLACE FUNCTION {{schema}}.reset_job_state(
    p_id BIGINT
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_status SMALLINT;
    v_namespace_id INT;
    v_lineage_root_id BIGINT;
    v_definition_id INT;
    v_tenant_id INT;
    v_execution_number INT;
    v_audit_level SMALLINT;
    v_job_ref UUID;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref
    INTO v_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_audit_level, v_job_ref
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = p_id
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN 1;
    END IF;

    DELETE FROM {{schema}}.checkpoints WHERE job_id = p_id;
    DELETE FROM {{schema}}.steps WHERE job_id = p_id;
    DELETE FROM {{schema}}.results WHERE job_id = p_id;

    IF v_audit_level = 20 /* JobAuditLevelCode.Audit */ THEN
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            job_ref,
            execution_number,
            lineage_root_id,
            definition_id,
            tenant_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        VALUES (
            81 /* EventCode.JobStateReset */,
            now(),
            v_namespace_id,
            50 /* ActorCode.Job */,
            p_id::varchar,
            p_id,
            v_job_ref,
            v_execution_number,
            COALESCE(v_lineage_root_id, p_id),
            v_definition_id,
            v_tenant_id,
            NULL,
            v_status,
            v_status,
            NULL,
            NULL,
            NULL,
            NULL);
    END IF;

    RETURN 1;
END;
$$;
