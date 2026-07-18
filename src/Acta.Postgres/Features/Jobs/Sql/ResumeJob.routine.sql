CREATE OR REPLACE FUNCTION {{schema}}.resume_job(
    p_id               BIGINT,
    p_actor_code       SMALLINT,
    p_actor_key         VARCHAR,
    p_reason_code      SMALLINT,
    p_reason_message   VARCHAR,
    p_next_run_at_utc  TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE(action SMALLINT, status_code SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status       SMALLINT;
    v_namespace_id  SMALLINT;
    v_lineage_root_id   BIGINT;
    v_definition_id INT;
    v_tenant_id         INT;
    v_execution_number  INT;
    v_audit_level       SMALLINT;
    v_job_ref           UUID;
BEGIN
    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref
      INTO v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_audit_level, v_job_ref
      FROM {{schema}}.runtimes r
      JOIN {{schema}}.jobs j ON j.id = r.job_id
     WHERE r.job_id = p_id
     FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT;
        RETURN;
    END IF;

    IF v_from_status <> 30 /* JobStatusCode.Paused */ THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    UPDATE {{schema}}.runtimes
       SET status_code      = 10 /* JobStatusCode.Ready */,
           next_run_at_utc   = COALESCE(p_next_run_at_utc, now()),
           modified_at_utc   = now(),
           version           = version + 1
     WHERE job_id = p_id;

    IF v_audit_level = 20 /* JobAuditLevelCode.Audit */ THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            72 /* JobEventCode.JobResumed */, now(), v_namespace_id,
            p_actor_code, p_actor_key,
            p_id, v_job_ref, v_execution_number,
            COALESCE(v_lineage_root_id, p_id), v_definition_id, v_tenant_id,
            NULL,
            30 /* JobStatusCode.Paused */, 10 /* JobStatusCode.Ready */,
            NULL, NULL,
            p_reason_code, p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, 10 /* JobStatusCode.Ready */::SMALLINT;
END;
$$;
